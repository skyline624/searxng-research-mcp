using System.Collections.Concurrent;
using System.Text;
using ServeurIa.Core.Llm;
using ServeurIa.Core.Tasks;
using ServeurIa.Core.Web;
using ServeurIa.Modules.Search;
using ServeurIa.Modules.Search.Agent;
using ServeurIa.Modules.Search.Browser;
using ServeurIa.Modules.Search.Pages;
using ServeurIa.Modules.Search.Providers;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ServeurIa.Tests.Search;

/// <summary>An <see cref="ILlmClient"/> that answers from a function and remembers what it was asked.</summary>
internal sealed class ScriptedLlm(Func<IReadOnlyList<(string role, string text)>, LlmOptions?, string> reply) : ILlmClient
{
    public sealed record Call(IReadOnlyList<(string role, string text)> Messages, int? MaxTokens, LlmOptions? Options);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public void Validate() { }

    public IAsyncEnumerable<string> CompleteAsync(Guid accountId, string? model, IReadOnlyList<(string role, string text)> messages, int? maxTokens = null, LlmOptions? options = null, CancellationToken ct = default)
    {
        Calls.Enqueue(new Call(messages, maxTokens, options));
        return Words(reply(messages, options));
    }

    /// <summary>The system prompt and the user turn of the call number <paramref name="index"/>.</summary>
    public (string System, string User) Prompt(int index)
    {
        var call = Calls.ElementAt(index);
        return (call.Messages.First(m => m.role == "system").text, call.Messages.Last(m => m.role == "user").text);
    }

    private static async IAsyncEnumerable<string> Words(string text)
    {
        foreach (var word in text.Split(' '))
        {
            yield return word + " ";
            await Task.Yield();
        }
    }
}

/// <summary>A search provider with canned answers per query.</summary>
internal sealed class FakeProvider(Func<SearchQuery, SearchResponse>? answer = null, bool configured = true) : ISearchProvider
{
    public ConcurrentQueue<SearchQuery> Queries { get; } = new();

    public string Name => "fake";

    public bool IsConfigured => configured;

    public Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        Queries.Enqueue(query);
        return answer is null ? Task.FromResult(new SearchResponse([], [])) : Task.FromResult(answer(query));
    }

    public Task<ProviderHealth> HealthAsync(CancellationToken ct) => Task.FromResult(new ProviderHealth(configured, configured, null, ["fake"]));

    public static SearchResult Result(string url, string title = "A page", string snippet = "snippet") => new(title, url, snippet, "fake", null, 1);
}

/// <summary>Pages by address; an address that is not there is a 404. Remembers the options of each fetch.</summary>
internal sealed class FakePages : IWebFetcher
{
    private readonly Dictionary<string, (string Type, byte[] Body)> _pages = [];
    private readonly Dictionary<string, WebFetchException> _failures = [];

    public ConcurrentQueue<(Uri Url, FetchOptions Options)> Fetches { get; } = new();

    public FakePages Html(string url, string html) => Add(url, "text/html", Encoding.UTF8.GetBytes(html));

    public FakePages Fail(string url, WebFetchException error)
    {
        _failures[url] = error;
        return this;
    }

    public FakePages Add(string url, string type, byte[] body)
    {
        _pages[url] = (type, body);
        return this;
    }

    public Task<FetchedPage> FetchAsync(Uri uri, FetchOptions? options = null, CancellationToken ct = default)
    {
        Fetches.Enqueue((uri, options ?? new FetchOptions()));
        if (_failures.TryGetValue(uri.ToString(), out var failure))
        {
            throw failure;
        }

        if (!_pages.TryGetValue(uri.ToString(), out var page))
        {
            throw new WebFetchException(WebFetchFailure.HttpStatus, $"{uri} answered HTTP 404.", 404);
        }

        return Task.FromResult(new FetchedPage(uri, 200, page.Type, null, page.Body));
    }
}

internal sealed class FakeRenderer(Func<Uri, string>? render = null, bool available = true) : IPageRenderer
{
    public ConcurrentQueue<Uri> Rendered { get; } = new();

    public bool IsAvailable => available;

    public Task<string> RenderAsync(Uri url, CancellationToken ct)
    {
        Rendered.Enqueue(url);
        return render is null ? throw new BrowserException("no browser") : Task.FromResult(render(url));
    }

    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(available && render is not null);
}

internal sealed class RecordingProgress : ITaskProgress
{
    private readonly object _lock = new();

    public List<(long Done, string? Stage)> Reports { get; } = [];

    public Task ReportAsync(long done, long total, string? stage = null)
    {
        lock (_lock)
        {
            Reports.Add((done, stage));
        }

        return Task.CompletedTask;
    }

    public List<string?> Stages
    {
        get
        {
            lock (_lock)
            {
                return [.. Reports.Select(r => r.Stage)];
            }
        }
    }
}

/// <summary>Remembers what the agent reports while it runs.</summary>
internal sealed class RecordingSink : IResearchSink
{
    private readonly object _lock = new();

    public List<ResearchTraceView> Traces { get; } = [];

    public List<string> Partials { get; } = [];

    public ResearchTraceView Last
    {
        get
        {
            lock (_lock)
            {
                return Traces[^1];
            }
        }
    }

    public Task TraceChangedAsync(ResearchTrace trace)
    {
        lock (_lock)
        {
            Traces.Add(trace.Snapshot());
        }

        return Task.CompletedTask;
    }

    public Task PartialAnswerAsync(string text)
    {
        lock (_lock)
        {
            Partials.Add(text);
        }

        return Task.CompletedTask;
    }
}

internal static class SearchTestKit
{
    public static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            builder.AddPage(595, 842).AddText(text, 12, new PdfPoint(50, 780), font);
        }

        return builder.Build();
    }

    public static SearchSettings Settings(SearchSettings? settings = null) => settings ?? new();

    /// <summary>An article page whose text is long enough to count as readable, around <paramref name="body"/>.</summary>
    public static string Article(string title, string body) =>
        $"<html lang=\"en\"><head><title>{title}</title></head><body><article><h1>{title}</h1><p>{body}</p>" +
        "<p>This filler paragraph exists so that the page is long enough to be read as a real article by the content extractor, with several sentences, some commas, and a few more words to be safe.</p>" +
        "<p>Another filler paragraph follows, in the same spirit, mentioning nothing in particular but adding weight to the page, so that the readable text is clearly above the threshold used by the page service.</p>" +
        "</article></body></html>";
}
