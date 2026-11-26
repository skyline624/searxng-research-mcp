namespace ServeurIa.Modules.Search.Providers;

/// <summary>
/// What to look for. <see cref="Categories"/> are SearXNG's: <c>general</c>, <c>science</c>, <c>it</c>, <c>news</c>, <c>social media</c>,
/// <c>onions</c> (<see cref="SearchCategories.Normalize"/> also accepts the names the API and MCP tools use: <c>web</c>,
/// <c>academic</c>, <c>discussions</c>, <c>onion</c>).
/// </summary>
public sealed record SearchQuery(
    string Text,
    string? Language = null,
    IReadOnlyList<string>? Categories = null,
    string? TimeRange = null,
    int Page = 1,
    int MaxResults = 10, IReadOnlyList<string>? Engines = null);

public sealed record SearchResult(string Title, string Url, string Snippet, string? Engine, string? PublishedDate, double Score);

/// <summary>The structured feedback of a search: how many results the engines found in total, how long it took, and what SearXNG's own corrections and suggestions say about the query.</summary>
public sealed record QueryMeta(int? NumberOfResults, int? QueryDurationMs, string? Corrections, IReadOnlyList<string>? Suggestions)
{
    public static readonly QueryMeta Empty = new(null, null, null, null);
}

public sealed record SearchResponse(IReadOnlyList<SearchResult> Results, IReadOnlyList<string> UnresponsiveEngines, QueryMeta? Meta = null);

public sealed record ProviderHealth(bool Configured, bool Reachable, string? Error, IReadOnlyList<string> Engines);

public sealed class SearchProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A source of web search results. V1 has one, SearXNG; an API provider (Brave...) may be added behind this.</summary>
public interface ISearchProvider
{
    string Name { get; }

    /// <summary>False when no address is configured: the module then reports itself degraded.</summary>
    bool IsConfigured { get; }

    /// <exception cref="SearchProviderException">The provider is unreachable or answered badly.</exception>
    Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct);

    Task<ProviderHealth> HealthAsync(CancellationToken ct);
}

public static class SearchCategories
{
    public static readonly IReadOnlyList<string> Known = ["general", "science", "it", "news", "social media", "onions"];

    /// <summary>The SearXNG category of a name; null when unknown. <c>onions</c> is SearXNG's category of the ahmia engine,
    /// the index of the Tor network: it answers only when the query names it, and only through the Tor proxy.</summary>
    public static string? Normalize(string name) => name.Trim().ToLowerInvariant() switch
    {
        "web" or "general" => "general",
        "academic" or "science" => "science",
        "discussions" or "social media" or "social" => "social media",
        "it" => "it",
        "news" => "news",
        "onion" or "onions" => "onions",
        _ => null,
    };
}
