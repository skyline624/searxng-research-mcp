using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServeurIa.Core.Web;
using ServeurIa.Infrastructure.Web;

namespace ServeurIa.Modules.Search;

#pragma warning disable CA5351 // MD5 encrypts nothing here: it is Ahmia's published hash format, not a key.

/// <summary>
/// The mandatory blocklist of .onion addresses: Ahmia's public hashlist of the sites it bans (child sexual abuse material),
/// one lowercase MD5 hex per line of the address with its <c>.onion</c> suffix (<c>https://ahmia.fi/blacklist/</c>).
/// While Tor is configured, no .onion result is shown and no .onion page is fetched until the list is verified, and the
/// addresses it lists are refused on every fetch. The list is downloaded over the clearnet through the guarded
/// fetcher at startup and every 24 hours; a failed refresh retries after 15 minutes and keeps the previous list.
/// </summary>
public sealed class OnionBlocklist(IOptions<SearchOptions> options, SafeFetcher fetcher, ILogger<OnionBlocklist> logger) : IHostedService, IDisposable
{
    /// <summary>Ahmia's public blocklist, the default source (see <c>https://ahmia.fi/blacklist/</c>).</summary>
    public const string DefaultUrl = "https://ahmia.fi/blacklist/banned/";

    private static readonly TimeSpan RefreshDelay = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    private readonly SafeFetcher _fetcher = fetcher;
    private readonly ILogger<OnionBlocklist> _logger = logger;

    /// <summary>The hashes of the loaded list; replaced atomically (read with <see cref="Volatile.Read"/>).</summary>
    private HashSet<string> _hashes = [];
    private volatile bool _ready;
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    /// <summary>Blocklist URL when Tor is configured.</summary>
    public string? Url => options.Value.TorProxy is null ? null
        : string.IsNullOrWhiteSpace(options.Value.OnionBlocklistUrl) ? DefaultUrl : options.Value.OnionBlocklistUrl;

    /// <summary>True when the list is loaded: the filter then refuses the addresses it lists. False is stricter:
    /// .onion results and fetches are refused entirely until the list is verified.</summary>
    public bool Ready => _ready;

    /// <summary>Replaces the loaded list; also how tests seed a fake one.</summary>
    public void Load(IEnumerable<string> hashes)
    {
        _hashes = [.. hashes.Where(h => h.Length == 32).Select(h => h.ToLowerInvariant())];
        _ready = true;
    }

    /// <summary>Whether a .onion address may be shown as a search result. Everything but .onion passes;
    /// .onion passes only when the blocklist is loaded and the host is not listed.</summary>
    public bool Allows(Uri uri)
    {
        if (!IsOnion(uri.Host))
        {
            return true;
        }

        return Url is not null && _ready && !IsBanned(uri.Host);
    }

    /// <summary>Refuses a .onion address the mandatory filter cannot allow: a listed host, or any address while the
    /// list itself could not be verified. Non-.onion addresses pass.</summary>
    public void Guard(Uri uri)
    {
        if (!IsOnion(uri.Host) || Url is null)
        {
            return;
        }

        if (!_ready)
        {
            throw new WebFetchException(WebFetchFailure.BlockedAddress,
                $"The blocklist of .onion addresses ({Url}) could not be loaded: the address is refused until the filter is verified.");
        }

        if (IsBanned(uri.Host))
        {
            throw new WebFetchException(WebFetchFailure.BlockedAddress,
                $"{uri.Host} is on the mandatory .onion blocklist (abuse material) and is not fetched.");
        }
    }

    internal static bool IsOnion(string host) => host.TrimEnd('.').EndsWith(".onion", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the service behind the host is listed. Ahmia hashes the service name without subdomains.</summary>
    public bool IsBanned(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length <= ".onion".Length || !IsOnion(normalized))
        {
            return false;
        }

        var suffixStart = normalized.Length - ".onion".Length;
        var subdomainEnd = normalized.LastIndexOf('.', suffixStart - 1);
        var service = normalized[(subdomainEnd + 1)..];
        var hash = Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(service))).ToLowerInvariant();
        return Volatile.Read(ref _hashes).Contains(hash);
    }

    // ---- hosted service: the first load before any request is served, then refreshes in the background ----

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        if (Url is null)
        {
            _ready = true;
            return;
        }

        try
        {
            await LoadAsync(_stopping.Token);
        }
        catch (Exception e)
        {
            // The brain starts anyway: the filter is stricter while the list is missing (no .onion at all),
            // and the refresh loop retries in a few minutes.
            _logger.LogWarning("The .onion blocklist could not be loaded from {Url}: {Message}", Url, e.Message);
        }

        _loop = Task.Run(() => LoopAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _stopping?.Cancel();
            await _loop;
        }
        catch (ObjectDisposedException)
        {
            // The token source may already be disposed (host stopped and disposed), the loop is over either way.
        }
        catch (OperationCanceledException) when (_stopping is { IsCancellationRequested: true })
        {
            // Stopping: expected.
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_ready ? RefreshDelay : RetryDelay, ct);
            try
            {
                await LoadAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning("The .onion blocklist could not be refreshed from {Url}: {Message}", Url, e.Message);
            }
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        const string hex = "0123456789abcdefABCDEF";
        var page = await _fetcher.FetchAsync(new Uri(Url!, UriKind.Absolute),
            new FetchOptions { Timeout = TimeSpan.FromSeconds(10), MaxBytes = 16 * 1024 * 1024 }, ct);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Encoding.UTF8.GetString(page.Body).Split('\n'))
        {
            var hash = line.Trim();
            if (hash.Length == 32 && hash.All(hex.Contains))
            {
                hashes.Add(hash.ToLowerInvariant());
            }
        }

        if (hashes.Count == 0)
        {
            throw new WebFetchException(WebFetchFailure.HttpStatus, $"The blocklist of .onion addresses from {Url} is empty.");
        }

        Load(hashes);
        _logger.LogInformation("The mandatory .onion blocklist lists {Count} addresses, from {Url}.", hashes.Count, Url);
    }

    public void Dispose() => _stopping?.Dispose();
}
