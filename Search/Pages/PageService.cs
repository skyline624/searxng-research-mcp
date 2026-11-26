using System.Text;
using ServeurIa.Core.Web;
using ServeurIa.Modules.Search.Browser;

namespace ServeurIa.Modules.Search.Pages;

/// <summary>How a page is read: the fetch (robots, rate key) and whether the browser may be used.</summary>
public sealed record PageRequest(Uri Url, int MaxChars = 20000, bool? Render = null, bool RespectRobots = true, string? RateKey = null);

/// <summary>
/// A page, cleaned. <see cref="Markdown"/> is cut at <c>MaxChars</c> (<see cref="Truncated"/>). <see cref="Bytes"/> is the size
/// downloaded, <see cref="Pages"/> the page count of a PDF, <see cref="Robots"/> <c>allowed</c> (robots.txt was read and permits it)
/// or <c>ignored</c> (not consulted: switched off); a page it forbids is refused with 403, not returned.
/// </summary>
public sealed record PageContent(
    string Url,
    string FinalUrl,
    string Title,
    string? Author,
    string? Language,
    DateTimeOffset? Published,
    string ContentType,
    bool Rendered,
    bool Truncated,
    string Markdown,
    long Bytes = 0,
    int? Pages = null,
    string Robots = "allowed");

/// <summary>
/// Reads a page for the module: the direct fetch (public addresses only), the cleaning by content type, and, when the readable text
/// is too short or the caller asks (<c>render: true</c>), the same page in the browser, whose network goes through the filtering proxy.
/// A browser that fails leaves the direct result.
/// </summary>
public sealed class PageService(IWebFetcher fetcher, IPageRenderer renderer, SearchSettings current)
{
    /// <summary>Under this many characters of readable text, a page is probably a JavaScript shell.</summary>
    public const int MinReadableChars = 400;

    public async Task<PageContent> GetAsync(PageRequest request, CancellationToken ct)
    {
        var page = await fetcher.FetchAsync(
            request.Url,
            new FetchOptions { RespectRobots = request.RespectRobots && current.RespectRobots, PolitePerHost = true, RateKey = request.RateKey },
            ct);
        var isOnion = OnionBlocklist.IsOnion(page.FinalUri.Host);

        CleanedPage cleaned;
        var rendered = false;
        switch (page.ContentType)
        {
            case "application/pdf":
                cleaned = PageCleaner.FromPdf(page.Body);
                break;
            case "text/html" or "application/xhtml+xml":
                cleaned = PageCleaner.FromHtml(Decode(page), page.FinalUri);
                if (!isOnion && request.Render != false && renderer.IsAvailable && current.BrowserEnabled
                    && (request.Render == true || PageCleaner.ToPlain(cleaned.Markdown).Length < MinReadableChars))
                {
                    if (await TryRenderAsync(page.FinalUri, ct) is { } viaBrowser
                        && (request.Render == true || PageCleaner.ToPlain(viaBrowser.Markdown).Length > PageCleaner.ToPlain(cleaned.Markdown).Length))
                    {
                        cleaned = viaBrowser;
                        rendered = true;
                    }
                }

                break;
            default:
                cleaned = PageCleaner.FromText(Decode(page), page.FinalUri);
                break;
        }

        var max = Math.Clamp(request.MaxChars, 200, 200_000);
        var truncated = cleaned.Markdown.Length > max;
        return new PageContent(
            request.Url.ToString(),
            page.FinalUri.ToString(),
            cleaned.Title,
            cleaned.Author,
            cleaned.Language,
            cleaned.Published,
            page.ContentType,
            rendered,
            truncated,
            truncated ? cleaned.Markdown[..max] : cleaned.Markdown,
            page.Body.Length,
            cleaned.PageCount,
            !isOnion && request.RespectRobots && current.RespectRobots ? "allowed" : "ignored");
    }

    private async Task<CleanedPage?> TryRenderAsync(Uri url, CancellationToken ct)
    {
        try
        {
            return PageCleaner.FromHtml(await renderer.RenderAsync(url, ct), url);
        }
        catch (Exception e) when (e is BrowserException || (e is TimeoutException))
        {
            return null;
        }
    }

    private static string Decode(FetchedPage page)
    {
        Encoding encoding;
        try
        {
            encoding = page.Charset is { Length: > 0 } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(page.Body);
    }
}
