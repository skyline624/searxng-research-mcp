namespace ServeurIa.Core.Web;

/// <summary>Why a fetch was refused or failed. The message of <see cref="WebFetchException"/> says it in words.</summary>
public enum WebFetchFailure
{
    InvalidUrl,
    BlockedScheme,
    BlockedPort,
    BlockedAddress,
    Credentials,
    RobotsDisallowed,
    TooManyRedirects,
    TooLarge,
    Timeout,
    ContentType,
    HttpStatus,
    RateLimited,
    Network,
}

public sealed class WebFetchException(WebFetchFailure reason, string message, int? httpStatus = null, Exception? inner = null)
    : Exception(message, inner)
{
    public WebFetchFailure Reason { get; } = reason;

    /// <summary>The HTTP status when <see cref="Reason"/> is <see cref="WebFetchFailure.HttpStatus"/>.</summary>
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>What one fetch may do. The defaults suit reading a web page for an agent.</summary>
public sealed record FetchOptions
{
    public static readonly IReadOnlyList<string> PageContentTypes =
        ["text/html", "application/xhtml+xml", "text/*", "application/pdf", "application/json"];

    /// <summary>The body is cut off, and the fetch fails, past this many bytes (after decompression).</summary>
    public int MaxBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>For the whole fetch: redirects, robots.txt and body included.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Allowed media types: exact (<c>application/pdf</c>) or a whole family (<c>text/*</c>).</summary>
    public IReadOnlyList<string> ContentTypes { get; init; } = PageContentTypes;

    /// <summary>Read the robots.txt of the host first, and refuse what it forbids to <c>ServeurIA-Research</c> or <c>*</c>.</summary>
    public bool RespectRobots { get; init; }

    /// <summary>Wait for the per-host politeness limits (at most 2 requests at a time, a pause between two).</summary>
    public bool PolitePerHost { get; init; }

    /// <summary>Counted against the per-key limit (requests per minute); null means no per-key limit.</summary>
    public string? RateKey { get; init; }

    public int MaxRedirects { get; init; } = 5;
}

/// <summary>A fetched resource: the address it ended on after redirects, and its body.</summary>
public sealed record FetchedPage(Uri FinalUri, int Status, string ContentType, string? Charset, byte[] Body);

/// <summary>
/// Reads a URL on the public internet for the brain (page reading, image download). Implementations must never reach a
/// private network: see <c>SafeFetcher</c> in the infrastructure.
/// </summary>
public interface IWebFetcher
{
    /// <exception cref="WebFetchException">The URL is refused or the fetch failed.</exception>
    Task<FetchedPage> FetchAsync(Uri uri, FetchOptions? options = null, CancellationToken ct = default);
}
