using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServeurIa.Infrastructure.Web;
using ServeurIa.Modules.Search;
using ServeurIa.Modules.Search.Browser;

namespace ServeurIa.Tests.Search;

public class BrowserProxyTests
{
    [Fact]
    public async Task StoppingAndDisposingTheHostedProxyClosesItOnce()
    {
        var config = Options.Create(new SearchOptions
        {
            BrowserUrl = "ws://localhost:3000/",
            BrowserProxyPort = 0,
        });
        using var fetcher = new SafeFetcher();
        using var service = new BrowserProxyService(config, fetcher, NullLogger<BrowserProxyService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
