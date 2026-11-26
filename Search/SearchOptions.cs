namespace ServeurIa.Modules.Search;

public sealed class SearchOptions
{
    /// <summary>Base URL of the SearXNG companion (<c>http://searxng:8080</c>). Empty: no web search.</summary>
    public string? SearxngUrl { get; set; }

    /// <summary>WebSocket URL of the Playwright companion (<c>ws://playwright:3000/</c>). Empty: no JavaScript rendering.</summary>
    public string? BrowserUrl { get; set; }

    /// <summary>Port of the filtering proxy used by Playwright.</summary>
    public int BrowserProxyPort { get; set; } = 3128;

    public string BrowserProxyListenAddress { get; set; } = "127.0.0.1";

    /// <summary>How the browser container reaches the proxy, for example <c>http://mcp:3128</c>.</summary>
    public string? BrowserProxyUrl { get; set; }

    /// <summary>The partial answer of a research is saved at most this often while it is written (milliseconds).</summary>
    public int PartialAnswerIntervalMs { get; set; } = 1000;

    /// <summary>SOCKS5 proxy for reading .onion pages.</summary>
    public string? TorProxyUrl { get; set; }

    /// <summary>
    /// Where the mandatory blocklist of .onion addresses is downloaded from when Tor is configured (see
    /// <see cref="OnionBlocklist"/>). Defaults to Ahmia's public hashlist.
    /// </summary>
    public string? OnionBlocklistUrl { get; set; }

    public bool HasSearxng => Uri.TryCreate(SearxngUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";

    public bool HasBrowser => !string.IsNullOrWhiteSpace(BrowserUrl);

    /// <summary>True when a Tor proxy is configured, whatever the shape of the address.</summary>
    public bool HasTor => Uri.TryCreate(TorProxyUrl, UriKind.Absolute, out _);

    /// <summary>The Tor proxy as an address with a port; port falls back to Tor's SOCKS5 default.</summary>
    public Uri? TorProxy => Uri.TryCreate(TorProxyUrl, UriKind.Absolute, out var proxy) && proxy.Host.Length > 0 ? proxy : null;
}
