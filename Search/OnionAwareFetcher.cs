using System.Net;
using System.Net.Sockets;
using System.Text;
using ServeurIa.Core.Web;
using ServeurIa.Infrastructure.Web;

namespace ServeurIa.Modules.Search;

/// <summary>
/// .onion addresses go through the configured Tor proxy: the system cannot resolve them, so the direct guarded fetcher is thrown
/// away and the page is fetched through the proxy instead (SOCKS5, TLS end to end with the site, same size and time caps and
/// content-type allowlist, redirects re-validated). Robots.txt is not consulted and there is no per-host pause: the address is
/// Tor's own, and the proxy does not know how to identify the site. Every .onion address, on every hop, is checked against the
/// mandatory <see cref="OnionBlocklist"/> (see its doc). Everything else goes to the guarded SafeFetcher.
/// </summary>
public sealed class OnionAwareFetcher : IWebFetcher, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly SafeFetcher _inner;
    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly int[]? _ports;
    private readonly OnionBlocklist _blocklist;
    private readonly string _proxyHost;
    private readonly int _proxyPort;

    public OnionAwareFetcher(SafeFetcher inner, Uri proxy, SafeFetcherOptions options, OnionBlocklist blocklist)
    {
        _inner = inner;
        _userAgent = options.UserAgent;
        _ports = options.AllowedPorts;
        _blocklist = blocklist;
        _proxyHost = proxy.Host;
        _proxyPort = proxy.Port is -1 ? 9050 : proxy.Port;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = ConnectThroughProxyAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    public async Task<FetchedPage> FetchAsync(Uri uri, FetchOptions? options = null, CancellationToken ct = default)
    {
        options ??= new FetchOptions();
        if (!OnionBlocklist.IsOnion(uri.Host))
        {
            return await _inner.FetchAsync(uri, options, ct);
        }

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(options.Timeout);
        try
        {
            return await FollowAsync(uri, options, total.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebFetchException(WebFetchFailure.Timeout, $"{uri} took longer than {options.Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException e)
        {
            throw Wrap(e, uri);
        }
    }

    // ---- connection ----

    /// <summary>Opens the tunnel to the .onion site through the SOCKS5 proxy (no auth; the proxy resolves the onion name).</summary>
    private async ValueTask<Stream> ConnectThroughProxyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(_proxyHost, _proxyPort, ct);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            // SOCKS5 (RFC 1928), no authentication, hostname target: the proxy resolves the onion name.
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            var method = await ReadExactlyAsync(stream, 2, ct);
            if (method[0] != 0x05 || method[1] != 0x00)
            {
                throw new WebFetchException(WebFetchFailure.Network, $"The SOCKS5 proxy refused the no-auth method ({method[1]:X2}).");
            }

            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var hostBytes = Encoding.ASCII.GetBytes(host);
            var request = new byte[7 + hostBytes.Length];
            request[0] = 0x05;
            request[1] = 0x01;
            request[2] = 0x00;
            request[3] = 0x03;
            request[4] = (byte)hostBytes.Length;
            Array.Copy(hostBytes, 0, request, 5, hostBytes.Length);
            request[5 + hostBytes.Length] = (byte)(port >> 8);
            request[6 + hostBytes.Length] = (byte)port;
            await stream.WriteAsync(request, ct);

            var reply = await ReadExactlyAsync(stream, 4, ct);
            if (reply[0] != 0x05 || reply[1] != 0x00)
            {
                throw new WebFetchException(WebFetchFailure.Network, $"The SOCKS5 proxy refused the connection to '{host}' (status {reply[1]:X2}).");
            }

            await SkipAddressAsync(stream, reply[3], ct);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static async Task SkipAddressAsync(NetworkStream stream, byte atyp, CancellationToken ct)
    {
        switch (atyp)
        {
            case 0x01: // IPv4
                await ReadExactlyAsync(stream, 4, ct);
                break;
            case 0x04: // IPv6
                await ReadExactlyAsync(stream, 16, ct);
                break;
            case 0x03: // domain name
                var length = await ReadExactlyAsync(stream, 1, ct);
                await ReadExactlyAsync(stream, length[0], ct);
                break;
            default:
                throw new WebFetchException(WebFetchFailure.Network, $"The SOCKS5 proxy returned an unknown address type {atyp:X2}.");
        }

        await ReadExactlyAsync(stream, 2, ct); // port
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                throw new WebFetchException(WebFetchFailure.Network, "The SOCKS5 proxy closed the connection.");
            }

            read += n;
        }

        return buffer;
    }

    // ---- fetch ----

    private async Task<FetchedPage> FollowAsync(Uri start, FetchOptions options, CancellationToken ct)
    {
        var current = AsHttp(start);
        for (var hop = 0; hop <= options.MaxRedirects; hop++)
        {
            Validate(current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/pdf,application/json;q=0.9,*/*;q=0.5");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } location)
                {
                    throw new WebFetchException(WebFetchFailure.HttpStatus, $"{current} answered HTTP {status} without a Location.", status);
                }

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!OnionBlocklist.IsOnion(next.Host))
                {
                    throw new WebFetchException(WebFetchFailure.BlockedAddress, "An .onion page cannot redirect outside Tor.");
                }

                current = AsHttp(next);
                continue;
            }

            if (status is < 200 or >= 300)
            {
                throw new WebFetchException(WebFetchFailure.HttpStatus, $"{current} answered HTTP {status}.", status);
            }

            return await ReadAsync(current, response, options, ct);
        }

        throw new WebFetchException(WebFetchFailure.TooManyRedirects, $"{start} redirected more than {options.MaxRedirects} times.");
    }

    private static Uri AsHttp(Uri uri) => uri.Scheme == "onion" ? new UriBuilder(uri) { Scheme = "http" }.Uri : uri;

    private void Validate(Uri uri)
    {
        _blocklist.Guard(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw new WebFetchException(WebFetchFailure.BlockedScheme, "Only http and https addresses are allowed.");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new WebFetchException(WebFetchFailure.Credentials, "Addresses with a user name or password are refused.");
        }

        if (_ports is { } ports && !ports.Contains(uri.Port))
        {
            throw new WebFetchException(WebFetchFailure.BlockedPort, $"Port {uri.Port} is not allowed.");
        }
    }

    private static WebFetchException Wrap(HttpRequestException e, Uri uri)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is WebFetchException refused)
            {
                return refused;
            }
        }

        return new WebFetchException(WebFetchFailure.Network, $"{uri} could not be reached: {e.Message}", inner: e);
    }

    private static async Task<FetchedPage> ReadAsync(Uri uri, HttpResponseMessage response, FetchOptions options, CancellationToken ct)
    {
        var mediaType = (response.Content.Headers.ContentType?.MediaType ?? "").ToLowerInvariant();
        if (!options.ContentTypes.Any(t => t.EndsWith("/*", StringComparison.Ordinal)
                ? mediaType.StartsWith(t[..^1], StringComparison.Ordinal)
                : mediaType == t))
        {
            throw new WebFetchException(WebFetchFailure.ContentType, $"{uri} is '{mediaType}', which is not an allowed type.");
        }

        if (response.Content.Headers.ContentLength > options.MaxBytes)
        {
            throw TooLarge(uri, options);
        }

        using var buffer = new MemoryStream();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > options.MaxBytes)
            {
                throw TooLarge(uri, options);
            }
        }

        return new FetchedPage(uri, (int)response.StatusCode, mediaType, response.Content.Headers.ContentType?.CharSet, buffer.ToArray());
    }

    private static WebFetchException TooLarge(Uri uri, FetchOptions options) =>
        new(WebFetchFailure.TooLarge, $"{uri} is larger than {options.MaxBytes / (1024 * 1024.0):0.#} MB.");
}
