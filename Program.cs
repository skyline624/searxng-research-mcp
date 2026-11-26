using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServeurIa.Core.Llm;
using ServeurIa.Core.Web;
using ServeurIa.Infrastructure.Web;
using ServeurIa.Modules.Search;
using ServeurIa.Modules.Search.Agent;
using ServeurIa.Modules.Search.Browser;
using ServeurIa.Modules.Search.Pages;
using ServeurIa.Modules.Search.Providers;
using SearxngResearchMcp;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    DisableDefaults = true,
});

builder.Configuration
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables("RESEARCH_MCP_")
    .AddCommandLine(args);
if (builder.Configuration["config"] is { } configPath)
{
    builder.Configuration
        .AddJsonFile(Path.GetFullPath(configPath), optional: false)
        .AddEnvironmentVariables("RESEARCH_MCP_")
        .AddCommandLine(args);
}

builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddOptions<SearchOptions>().Bind(builder.Configuration.GetSection("Search"));
builder.Services.AddOptions<OpenAiOptions>().Bind(builder.Configuration.GetSection("OpenAI"));
var settings = builder.Configuration.GetSection("Pages").Get<SearchSettings>() ?? new SearchSettings();
if (settings.RequestsPerMinute is < 1 or > 6000 || settings.HostIntervalMs is < 0 or > 60_000)
{
    throw new InvalidOperationException("Check Pages:RequestsPerMinute (1–6000) and Pages:HostIntervalMs (0–60000).");
}

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new SafeFetcherOptions
{
    KeyRequestsPerMinute = settings.RequestsPerMinute,
    HostMinInterval = TimeSpan.FromMilliseconds(settings.HostIntervalMs),
});
builder.Services.AddSafeFetcher();
builder.Services.AddSingleton<OnionBlocklist>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnionBlocklist>());
builder.Services.AddSingleton<IWebFetcher>(sp =>
{
    var direct = sp.GetRequiredService<SafeFetcher>();
    var search = sp.GetRequiredService<IOptions<SearchOptions>>().Value;
    return search.TorProxy is { } proxy
        ? new OnionAwareFetcher(direct, proxy, sp.GetRequiredService<SafeFetcherOptions>(), sp.GetRequiredService<OnionBlocklist>())
        : direct;
});

builder.Services.AddHttpClient(SearxngProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<ILlmClient, OpenAiClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<ISearchProvider, SearxngProvider>();
builder.Services.AddSingleton<IPageRenderer, PlaywrightRenderer>();
builder.Services.AddHostedService<BrowserProxyService>();
builder.Services.AddSingleton<PageService>();
builder.Services.AddTransient<ResearchAgent>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SearchTools>();

await builder.Build().RunAsync();
