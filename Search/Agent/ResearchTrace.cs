using ServeurIa.Core.Web;

namespace ServeurIa.Modules.Search.Agent;

/// <summary>One search sent to the engines: the query, the engines that answered it (comma separated), and how many results came back.</summary>
public sealed record TraceQuery(string Text, string? Engine, int ResultCount, DateTimeOffset At);

/// <summary>What happened to one page of the research. <see cref="Status"/> is one of <see cref="PageStatus"/>.</summary>
public sealed record TracePage(
    int? Index,
    string Url,
    string? Title,
    string Domain,
    string Status,
    bool Rendered,
    long? Bytes,
    long? Ms,
    DateTimeOffset At);

public sealed record ResearchTraceView(IReadOnlyList<TraceQuery> Queries, IReadOnlyList<TracePage> Pages)
{
    public static readonly ResearchTraceView Empty = new([], []);
}

/// <summary>The values of <see cref="TracePage.Status"/>.</summary>
public static class PageStatus
{
    public const string Read = "read";
    public const string SkippedRobots = "skipped_robots";
    public const string Blocked = "blocked";
    public const string TooBig = "too_big";
    public const string Timeout = "timeout";
    public const string Error = "error";
    public const string Unsupported = "unsupported";

    public static string Of(Exception e) => e switch
    {
        WebFetchException { Reason: WebFetchFailure.RobotsDisallowed } => SkippedRobots,
        WebFetchException { Reason: WebFetchFailure.BlockedAddress or WebFetchFailure.BlockedPort or WebFetchFailure.BlockedScheme or WebFetchFailure.Credentials or WebFetchFailure.InvalidUrl } => Blocked,
        WebFetchException { Reason: WebFetchFailure.TooLarge } => TooBig,
        WebFetchException { Reason: WebFetchFailure.Timeout } => Timeout,
        WebFetchException { Reason: WebFetchFailure.ContentType } => Unsupported,
        SearchException { Status: 422 } => Unsupported,
        _ => Error,
    };
}

/// <summary>The queries and pages of a research as they happen; safe to fill from parallel reads.</summary>
public sealed class ResearchTrace
{
    private readonly object _lock = new();
    private readonly List<TraceQuery> _queries = [];
    private readonly List<TracePage> _pages = [];

    public void Add(TraceQuery query)
    {
        lock (_lock)
        {
            _queries.Add(query);
        }
    }

    public void Add(TracePage page)
    {
        lock (_lock)
        {
            _pages.Add(page);
        }
    }

    /// <summary>Gives the page the number of its source, once the sources are numbered.</summary>
    public void Number(string url, int index)
    {
        lock (_lock)
        {
            var at = _pages.FindIndex(p => p.Url == url);
            if (at >= 0)
            {
                _pages[at] = _pages[at] with { Index = index };
            }
        }
    }

    public ResearchTraceView Snapshot()
    {
        lock (_lock)
        {
            return new ResearchTraceView([.. _queries], [.. _pages]);
        }
    }
}

/// <summary>Where the agent reports what it is doing, beyond the stage: the trace, and the answer as it is written.</summary>
public interface IResearchSink
{
    Task TraceChangedAsync(ResearchTrace trace);

    /// <summary>The visible text written so far (thinking removed); called about once per <c>PartialAnswerIntervalMs</c>.</summary>
    Task PartialAnswerAsync(string text);
}
