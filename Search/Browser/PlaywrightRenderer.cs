using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using ServeurIa.Infrastructure.Web;

namespace ServeurIa.Modules.Search.Browser;

public sealed class BrowserException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gives the HTML of a page once its JavaScript has run.</summary>
public interface IPageRenderer
{
    /// <summary>False when no browser is configured.</summary>
    bool IsAvailable { get; }

    /// <exception cref="BrowserException">The browser could not be reached or the page did not load.</exception>
    Task<string> RenderAsync(Uri url, CancellationToken ct);

    /// <summary>True when the browser answers.</summary>
    Task<bool> PingAsync(CancellationToken ct);
}

/// <summary>
/// Drives the Playwright companion over its remote protocol (<c>BrowserType.ConnectAsync</c> to <c>playwright run-server</c>).
/// Every page gets its own context whose only network path is the brain's filtering proxy: names are resolved and addresses judged
/// there, so the browser cannot reach a private network even if the page asks for it (the container also has no other route,
/// see <c>deploy/compose.yaml</c>). Images, media and fonts are not loaded; service workers and downloads are off.
/// </summary>
public sealed class PlaywrightRenderer(IOptions<SearchOptions> options, SafeFetcherOptions fetcher) : IPageRenderer, IAsyncDisposable
{
    private static readonly string[] SkippedResources = ["image", "media", "font"];

    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly SemaphoreSlim _pages = new(2, 2);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public bool IsAvailable => options.Value.HasBrowser;

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await BrowserAsync(ct);
            return true;
        }
        catch (BrowserException)
        {
            return false;
        }
    }

    public async Task<string> RenderAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            throw new BrowserException("Only http and https pages can be rendered.");
        }

        await _pages.WaitAsync(ct);
        try
        {
            var browser = await BrowserAsync(ct);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                // Loopback goes through the proxy too (which refuses it): a page must not reach the host where the Playwright server
                // listens. Playwright already does this for a per-context proxy (checked with 1.64); the explicit bypass keeps it so.
                Proxy = new Proxy { Server = ProxyUrl(), Bypass = "<-loopback>" },
                UserAgent = fetcher.UserAgent,
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block,
                JavaScriptEnabled = true,
            });
            await context.RouteAsync("**/*", route => SkippedResources.Contains(route.Request.ResourceType) ? route.AbortAsync() : route.ContinueAsync());
            var page = await context.NewPageAsync();
            var response = await page.GotoAsync(url.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20_000 });
            // The proxy answers 403 for an address it refuses; a site's own error page is no content either.
            if (response is { Status: >= 400 })
            {
                throw new BrowserException($"The browser got HTTP {response.Status} for {url}.");
            }
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 6_000 });
            }
            catch (TimeoutException)
            {
                // Some pages never go idle (polling, ads): what is there now is what we read.
            }

            var html = await page.ContentAsync();
            return html.Length > 5 * 1024 * 1024 ? html[..(5 * 1024 * 1024)] : html;
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException)
        {
            throw new BrowserException($"The browser could not load {url}: {e.Message}", e);
        }
        finally
        {
            _pages.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }

    private string ProxyUrl() => options.Value.BrowserProxyUrl is { Length: > 0 } url ? url : $"http://127.0.0.1:{options.Value.BrowserProxyPort}";

    private async Task<IBrowser> BrowserAsync(CancellationToken ct)
    {
        await _connect.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            if (!IsAvailable)
            {
                throw new BrowserException("No browser is configured.");
            }

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.ConnectAsync(options.Value.BrowserUrl!, new BrowserTypeConnectOptions { Timeout = 10_000 });
            return _browser;
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
        {
            throw new BrowserException($"The browser companion is not reachable: {e.Message}", e);
        }
        finally
        {
            _connect.Release();
        }
    }
}
