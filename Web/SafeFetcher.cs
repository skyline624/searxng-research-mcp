using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServeurIa.Core.Web;

namespace ServeurIa.Infrastructure.Web;

/// <summary>Settings of <see cref="SafeFetcher"/>. The defaults are the production ones.</summary>
public sealed class SafeFetcherOptions
{
    public const string RobotsAgent = "ServeurIA-Research";

    /// <summary>Identifies the brain to the sites it reads.</summary>
    public string UserAgent { get; set; } = $"{RobotsAgent}/0.1 (+https://github.com/skyline624/searxng-research-mcp)";

    /// <summary>Ports a URL may name; null allows any port.</summary>
    public int[]? AllowedPorts { get; set; } = [80, 443];

    /// <summary>How long the robots.txt of a host is kept.</summary>
    public TimeSpan RobotsTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Pause between two requests to one host, for fetches that ask to be polite.</summary>
    public TimeSpan HostMinInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Requests to one host at the same time, for fetches that ask to be polite.</summary>
    public int HostConcurrency { get; set; } = 2;

    /// <summary>Requests per minute for one <see cref="FetchOptions.RateKey"/>.</summary>
    public int KeyRequestsPerMinute { get; set; } = 120;
}

/// <summary>Resolves a host name; replaced in tests to simulate DNS answers (rebinding).</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

internal sealed class SystemResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// An HTTP reader that can only reach the public internet. The address check is made when the connection is opened
/// (<see cref="SocketsHttpHandler.ConnectCallback"/>): the name is resolved there, the answer is judged by
/// <see cref="IpPolicy"/>, and the socket is opened to that very address, so a name that answers differently a second
/// time (DNS rebinding) cannot slip a private address in. Redirects are followed here, one by one, each judged again
/// (scheme, port, credentials, robots.txt, address). The body is capped, the content type must be allowed, and no
/// cookie or proxy of the environment is used.
/// </summary>
public sealed class SafeFetcher : IWebFetcher, IDisposable
{
    private static readonly TimeSpan RobotsErrorTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly SafeFetcherOptions _options;
    private readonly IpPolicy _policy;
    private readonly IHostResolver _resolver;
    private readonly TimeProvider _time;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, (RobotsRules Rules, DateTimeOffset Expires)> _robots = new();
    private readonly ConcurrentDictionary<string, HostGate> _hosts = new();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _keys = new();

    public SafeFetcher(SafeFetcherOptions? options = null, IpPolicy? policy = null, IHostResolver? resolver = null, TimeProvider? time = null)
    {
        _options = options ?? new SafeFetcherOptions();
        _policy = policy ?? IpPolicy.Public;
        _resolver = resolver ?? new SystemResolver();
        _time = time ?? TimeProvider.System;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Resolves <paramref name="host"/> and returns the addresses the policy lets through, in the resolver's order. Shared with the
    /// browser proxy, which opens its own sockets with the same rule. Throws when none is allowed.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, CancellationToken ct)
    {
        var bare = host.Trim('[', ']');
        var all = IPAddress.TryParse(bare, out var literal) ? [literal] : await ResolveSafelyAsync(bare, ct);
        var allowed = all.Where(_policy.IsAllowed).ToList();
        if (allowed.Count == 0)
        {
            throw new WebFetchException(
                WebFetchFailure.BlockedAddress,
                all.Length == 0 ? $"The host '{host}' does not resolve." : $"The host '{host}' is not on the public internet.");
        }

        return allowed;
    }

    /// <summary>Opens a TCP connection to a public address of <paramref name="host"/> (used by the browser proxy).</summary>
    public async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken ct)
    {
        if (_options.AllowedPorts is { } ports && !ports.Contains(port))
        {
            throw new WebFetchException(WebFetchFailure.BlockedPort, $"Port {port} is not allowed.");
        }

        Exception? last = null;
        foreach (var address in await ResolveAllowedAsync(host, ct))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token);
                return socket;
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                socket.Dispose();
                last = e;
            }
        }

        throw new WebFetchException(WebFetchFailure.Network, $"Could not connect to '{host}'.", inner: last);
    }

    public async Task<FetchedPage> FetchAsync(Uri uri, FetchOptions? options = null, CancellationToken ct = default)
    {
        options ??= new FetchOptions();
        CheckKey(options.RateKey);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(options.Timeout);
        try
        {
            return await FollowWrappedAsync(uri, options, total.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebFetchException(WebFetchFailure.Timeout, $"{uri} took longer than {options.Timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>Follows the redirects; what the handler throws (a refused connection arrives wrapped in an HttpRequestException) comes out as a <see cref="WebFetchException"/>.</summary>
    private async Task<FetchedPage> FollowWrappedAsync(Uri uri, FetchOptions options, CancellationToken ct)
    {
        try
        {
            return await FollowAsync(uri, options, ct);
        }
        catch (HttpRequestException e)
        {
            throw Wrap(e, uri);
        }
    }

    /// <summary>What a refused connection looks like once the handler has wrapped it (possibly more than once): the refusal itself, else a network failure.</summary>
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

    private async Task<FetchedPage> FollowAsync(Uri start, FetchOptions options, CancellationToken ct)
    {
        var current = start;
        for (var hop = 0; hop <= options.MaxRedirects; hop++)
        {
            Validate(current);
            if (options.RespectRobots && !await RobotsAllowAsync(current, ct))
            {
                throw new WebFetchException(WebFetchFailure.RobotsDisallowed, $"The robots.txt of {current.Host} forbids {current.PathAndQuery}.");
            }

            using var slot = options.PolitePerHost ? await EnterHostAsync(current, ct) : null;
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/pdf,application/json;q=0.9,*/*;q=0.5");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } location)
                {
                    throw new WebFetchException(WebFetchFailure.HttpStatus, $"{current} answered HTTP {status} without a Location.", status);
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
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

    private void Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw new WebFetchException(WebFetchFailure.BlockedScheme, "Only http and https addresses are allowed.");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new WebFetchException(WebFetchFailure.Credentials, "Addresses with a user name or password are refused.");
        }

        if (_options.AllowedPorts is { } ports && !ports.Contains(uri.Port))
        {
            throw new WebFetchException(WebFetchFailure.BlockedPort, $"Port {uri.Port} is not allowed.");
        }

        // A literal address is judged now; a name is judged by the connection, which sees the address it will use.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !_policy.IsAllowed(literal))
        {
            throw new WebFetchException(WebFetchFailure.BlockedAddress, $"The address {literal} is not on the public internet.");
        }
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

    // ---- connection ----

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        try
        {
            var socket = await ConnectSocketAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (WebFetchException e)
        {
            // HttpClient only lets HttpRequestException through from here; FetchAsync unwraps it.
            throw new HttpRequestException(e.Message, e);
        }
    }

    private async Task<IPAddress[]> ResolveSafelyAsync(string host, CancellationToken ct)
    {
        try
        {
            return await _resolver.ResolveAsync(host, ct);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    // ---- robots.txt ----

    private async Task<bool> RobotsAllowAsync(Uri uri, CancellationToken ct)
    {
        var origin = $"{uri.Scheme}://{uri.Authority}";
        var now = _time.GetUtcNow();
        if (!_robots.TryGetValue(origin, out var cached) || cached.Expires <= now)
        {
            cached = await LoadRobotsAsync(uri, origin, now, ct);
            _robots[origin] = cached;
        }

        return cached.Rules.IsAllowed(uri.PathAndQuery);
    }

    private async Task<(RobotsRules, DateTimeOffset)> LoadRobotsAsync(Uri uri, string origin, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var page = await FollowWrappedAsync(
                new Uri($"{origin}/robots.txt"),
                new FetchOptions { MaxBytes = 512 * 1024, Timeout = TimeSpan.FromSeconds(8), ContentTypes = ["text/*"], MaxRedirects = 3 },
                ct);
            return (RobotsRules.Parse(Encoding.UTF8.GetString(page.Body), SafeFetcherOptions.RobotsAgent), now + _options.RobotsTtl);
        }
        catch (WebFetchException e) when (e.Reason == WebFetchFailure.HttpStatus && e.HttpStatus is >= 400 and < 500)
        {
            // No robots.txt: everything is allowed.
            return (RobotsRules.AllowAll, now + _options.RobotsTtl);
        }
        catch (WebFetchException e) when (e.Reason != WebFetchFailure.BlockedAddress)
        {
            // shortcut: an unreachable robots.txt (5xx, timeout) allows the fetch for 5 minutes instead of forbidding the site as RFC 9309 says; harden if sites complain.
            return (RobotsRules.AllowAll, now + RobotsErrorTtl);
        }
    }

    // ---- limits ----

    private void CheckKey(string? key)
    {
        if (key is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var window = _keys.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() >= TimeSpan.FromMinutes(1))
            {
                window.Dequeue();
            }

            if (window.Count >= _options.KeyRequestsPerMinute)
            {
                throw new WebFetchException(WebFetchFailure.RateLimited, "Too many requests: wait a minute.");
            }

            window.Enqueue(now);
        }
    }

    private async Task<IDisposable> EnterHostAsync(Uri uri, CancellationToken ct)
    {
        if (_hosts.Count > 2000)
        {
            foreach (var idle in _hosts.Where(h => h.Value.Idle).Select(h => h.Key).ToList())
            {
                _hosts.TryRemove(idle, out _);
            }
        }

        var gate = _hosts.GetOrAdd(uri.Authority.ToLowerInvariant(), _ => new HostGate(_options.HostConcurrency));
        await gate.EnterAsync(_options.HostMinInterval, _time, ct);
        return gate;
    }

    /// <summary>Per-host concurrency and spacing: a request starts at least <c>interval</c> after the previous start.</summary>
    private sealed class HostGate(int concurrency) : IDisposable
    {
        private readonly SemaphoreSlim _slots = new(concurrency, concurrency);
        private readonly object _lock = new();
        private DateTimeOffset _nextStart = DateTimeOffset.MinValue;
        private int _inUse;

        public bool Idle => Volatile.Read(ref _inUse) == 0;

        public async Task EnterAsync(TimeSpan interval, TimeProvider time, CancellationToken ct)
        {
            Interlocked.Increment(ref _inUse);
            try
            {
                await _slots.WaitAsync(ct);
            }
            catch
            {
                Interlocked.Decrement(ref _inUse);
                throw;
            }

            try
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = time.GetUtcNow();
                    var start = _nextStart > now ? _nextStart : now;
                    _nextStart = start + interval;
                    wait = start - now;
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct);
                }
            }
            catch
            {
                Release();
                throw;
            }
        }

        public void Dispose() => Release();

        private void Release()
        {
            _slots.Release();
            Interlocked.Decrement(ref _inUse);
        }
    }
}

/// <summary>Registers <see cref="SafeFetcher"/> as the brain's <see cref="IWebFetcher"/> (once, whoever asks first).</summary>
public static class WebFetcherExtensions
{
    public static IServiceCollection AddSafeFetcher(this IServiceCollection services)
    {
        services.TryAddSingleton(new SafeFetcherOptions());
        services.TryAddSingleton(IpPolicy.Public);
        services.TryAddSingleton<SafeFetcher>(sp => new SafeFetcher(
            sp.GetRequiredService<SafeFetcherOptions>(),
            sp.GetRequiredService<IpPolicy>(),
            resolver: sp.GetService<IHostResolver>(),
            time: sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IWebFetcher>(sp => sp.GetRequiredService<SafeFetcher>());
        return services;
    }
}
