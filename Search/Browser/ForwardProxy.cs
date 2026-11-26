using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServeurIa.Core.Web;
using ServeurIa.Infrastructure.Web;

namespace ServeurIa.Modules.Search.Browser;

/// <summary>
/// The only way out of the browser: a small HTTP forward proxy (CONNECT for https, absolute-form GET and friends for http) that
/// opens its sockets through <see cref="SafeFetcher.ConnectSocketAsync"/>, so the browser is held to the same rules as the page
/// fetcher: the name is resolved here and the address judged at connect time (no loopback, private, link-local or metadata
/// address, no unusual port). The TLS of an https page stays end to end between the browser and the site. A refused request gets a
/// 403 and a reason; nothing else is logged about what is browsed.
/// </summary>
public sealed class ForwardProxy(SafeFetcher fetcher, ILogger logger) : IDisposable
{
    private const int MaxHeadBytes = 16 * 1024;
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(3);

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(64, 64);
    private TcpListener? _listener;

    /// <summary>The port the proxy listens on (useful when started with port 0).</summary>
    public int Port => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    public void Start(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
        _listener.Start();
        _ = AcceptLoopAsync(_listener);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        if (!await _slots.WaitAsync(0))
        {
            return;
        }

        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            lifetime.CancelAfter(MaxLifetime);
            var ct = lifetime.Token;
            var stream = client.GetStream();
            var (head, extra) = await ReadHeadAsync(stream, ct);
            if (head is null)
            {
                return;
            }

            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3)
            {
                await ReplyAsync(stream, 400, "Bad request", ct);
                return;
            }

            try
            {
                if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await TunnelAsync(stream, parts[1], extra, ct);
                }
                else
                {
                    await ForwardAsync(stream, parts[0], parts[1], parts[2], lines, extra, ct);
                }
            }
            catch (WebFetchException e)
            {
                logger.LogInformation("Browser proxy refused a request: {Reason}", e.Reason);
                await ReplyAsync(stream, e.Reason is WebFetchFailure.Network ? 502 : 403, e.Message, ct);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser went away, or the connection ran out of time.
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task TunnelAsync(NetworkStream client, string target, byte[] extra, CancellationToken ct)
    {
        var (host, port) = SplitHostPort(target, 443);
        using var upstream = await fetcher.ConnectSocketAsync(host, port, ct);
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await client.WriteAsync(reply, ct);
        await using var remote = new NetworkStream(upstream, ownsSocket: false);
        if (extra.Length > 0)
        {
            await remote.WriteAsync(extra, ct);
        }

        await PumpAsync(client, remote, ct);
    }

    private async Task ForwardAsync(NetworkStream client, string method, string target, string version, string[] lines, byte[] extra, CancellationToken ct)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new WebFetchException(WebFetchFailure.BlockedScheme, "The proxy forwards absolute http addresses and CONNECT only.");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new WebFetchException(WebFetchFailure.Credentials, "Addresses with a user name or password are refused.");
        }

        using var upstream = await fetcher.ConnectSocketAsync(uri.Host, uri.Port, ct);
        await using var remote = new NetworkStream(upstream, ownsSocket: false);
        // Origin-form request line, hop-by-hop proxy headers dropped, and one request per connection.
        var head = new StringBuilder($"{method} {uri.PathAndQuery} {version}\r\n");
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var name = line.Split(':')[0].Trim();
            if (!name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                head.Append(line).Append("\r\n");
            }
        }

        head.Append("Connection: close\r\n\r\n");
        await remote.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        if (extra.Length > 0)
        {
            await remote.WriteAsync(extra, ct);
        }

        await PumpAsync(client, remote, ct);
    }

    /// <summary>Copies both ways until either side closes or goes quiet for <see cref="IdleTimeout"/>.</summary>
    private static async Task PumpAsync(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toRemote = CopyAsync(a, b, both.Token);
        var toClient = CopyAsync(b, a, both.Token);
        await Task.WhenAny(toRemote, toClient);
        await both.CancelAsync();
        try
        {
            await Task.WhenAll(toRemote, toClient);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // One side closed: the other copy ends with it.
        }
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            var read = await from.ReadAsync(buffer, idle.Token);
            if (read == 0)
            {
                return;
            }

            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<(string? Head, byte[] Extra)> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(HeadTimeout);
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token);
            if (read == 0)
            {
                return (null, []);
            }

            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..length]);
            }
        }

        return (null, []);
    }

    private static async Task ReplyAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var text = status == 403 ? "Forbidden" : status == 502 ? "Bad Gateway" : "Bad Request";
        var body = Encoding.UTF8.GetBytes(message);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {text}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    private static (string Host, int Port) SplitHostPort(string target, int defaultPort)
    {
        var colon = target.LastIndexOf(':');
        if (colon > 0 && !target.EndsWith(']') && int.TryParse(target[(colon + 1)..], out var port))
        {
            return (target[..colon].Trim('[', ']'), port);
        }

        return (target.Trim('[', ']'), defaultPort);
    }
}

/// <summary>Runs <see cref="ForwardProxy"/> for the life of the brain, when a browser is configured.</summary>
public sealed class BrowserProxyService(IOptions<SearchOptions> options, SafeFetcher fetcher, ILogger<BrowserProxyService> logger) : IHostedService, IDisposable
{
    private ForwardProxy? _proxy;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.HasBrowser)
        {
            _proxy = new ForwardProxy(fetcher, logger);
            _proxy.Start(IPAddress.Parse(options.Value.BrowserProxyListenAddress), options.Value.BrowserProxyPort);
            logger.LogInformation("Browser proxy listening on port {Port}", _proxy.Port);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => Interlocked.Exchange(ref _proxy, null)?.Dispose();
}
