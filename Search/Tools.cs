using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using ServeurIa.Core.Llm;
using ServeurIa.Core.Tasks;
using ServeurIa.Modules.Search;
using ServeurIa.Modules.Search.Agent;
using ServeurIa.Modules.Search.Providers;

namespace SearxngResearchMcp;

public sealed record WebSearchResult(string Title, string Url, string Snippet, string? Engine, string? PublishedDate);

public sealed record WebSearchResponse(
    IReadOnlyList<WebSearchResult> Results,
    IReadOnlyList<string> UnresponsiveEngines,
    int? NumberOfResults,
    int? QueryDurationMs,
    string? Corrections,
    IReadOnlyList<string>? Suggestions);

[McpServerToolType]
public sealed class SearchTools(
    ISearchProvider provider,
    ResearchAgent agent,
    ILlmClient llm,
    IOptions<SearchOptions> searchOptions)
{
    [McpServerTool(Name = "web_search", ReadOnly = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Search SearXNG and return links, snippets and unavailable engines. Use research for a written, cited answer.")]
    public async Task<WebSearchResponse> WebSearch(
        [Description("Search query, up to 2000 characters.")] string query,
        [Description("Number of results from 1 to 50.")] int? max_results = null,
        [Description("Search language such as fr, en, fr-FR or all.")] string? language = null,
        [Description("Categories: web, academic, discussions, it, news, onion.")] List<string>? categories = null,
        [Description("Time range: day, week, month or year.")] string? time_range = null,
        [Description("Optional list of SearXNG engine names as configured on the instance.")] List<string>? engines = null,
        [Description("SearXNG results page, starting at 1.")] int page = 1,
        CancellationToken ct = default)
    {
        var input = Input(query, "query", 2000);
        if (language is not null && !Regex.IsMatch(language, @"\A(?:all|[a-z]{2}(?:-[a-z]{2})?)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new McpException("Use all or a language code such as fr or fr-FR.");
        }

        var selected = Categories(categories);
        if (page < 1)
        {
            throw new McpException("The results page must be at least 1.");
        }

        if (engines is { Count: > 30 } || engines?.Any(name =>
            string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Contains(',')) == true)
        {
            throw new McpException("Provide up to 30 individual SearXNG engine names.");
        }

        try
        {
            var result = await provider.SearchAsync(new SearchQuery(
                input, language, selected.Length > 0 ? selected : null, time_range, page,
                Math.Clamp(max_results ?? 10, 1, 50), engines), ct);
            return new WebSearchResponse(
                [.. result.Results.Select(r => new WebSearchResult(r.Title, r.Url, r.Snippet, r.Engine, r.PublishedDate))],
                result.UnresponsiveEngines, result.Meta?.NumberOfResults, result.Meta?.QueryDurationMs,
                result.Meta?.Corrections, result.Meta?.Suggestions);
        }
        catch (Exception error) when (error is SearchProviderException or ArgumentException)
        {
            throw new McpException(error.Message);
        }
    }

    [McpServerTool(Name = "research", ReadOnly = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Plan and refine web searches, read sources, and return a written answer with numbered citations. Waits for the result.")]
    public async Task<ResearchResult> Research(
        [Description("Research question, up to 2000 characters.")] string question,
        [Description("speed, balanced or quality.")] string? mode = null,
        [Description("Categories: web, academic, discussions, it, news, onion.")] List<string>? sources = null,
        [Description("Optional style or research angle, up to 2000 characters.")] string? instructions = null,
        [Description("Optional two-letter answer language, e.g. fr.")] string? language = null,
        [Description("Optional previous questions and answers for a follow-up.")] List<ResearchTurn>? history = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        var input = Input(question, "question", 2000);
        var selectedMode = mode?.Trim().ToLowerInvariant() ?? "balanced";
        if (!ModeProfile.Names.Contains(selectedMode))
        {
            throw new McpException("Use speed, balanced or quality.");
        }

        if (language is not null && !Regex.IsMatch(language, @"\A[a-zA-Z]{2}\z"))
        {
            throw new McpException("Use a two-letter answer language such as fr or en.");
        }

        if (instructions?.Length > 2000 || history?.Count > 8 ||
            history?.Any(t => t is null || string.IsNullOrWhiteSpace(t.Question) || t.Question.Length > 2000 ||
                t.Answer?.Length > 5000 || t.Sources is null || t.Sources.Count > 12 ||
                t.Sources.Any(s => s is null || s.Title is null || s.Title.Length > 500 || s.Url is null || s.Url.Length > 2048)) == true)
        {
            throw new McpException("The instructions or research history exceed the supported limits.");
        }

        var selected = Categories(sources);
        try
        {
            llm.Validate();
            return await agent.RunAsync(Guid.Empty,
                new ResearchRequest(input, selectedMode, selected.Length > 0 ? selected : ["web"],
                    instructions, language?.ToLowerInvariant(), history),
                new McpProgress(progress), ct);
        }
        catch (Exception error) when (error is LlmUnavailableException or SearchException)
        {
            throw new McpException(error.Message);
        }
    }

    private static string Input(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new McpException($"Provide a {name} between 1 and {maxLength} characters.");
        }

        return value.Trim();
    }

    private string[] Categories(List<string>? values)
    {
        var categories = (values ?? []).Select(name => name is null ? null : SearchCategories.Normalize(name)).ToArray();
        if (categories.Contains(null))
        {
            throw new McpException("Use web, academic, discussions, it, news or onion.");
        }

        if (categories.Contains("onions") && searchOptions.Value.TorProxy is null)
        {
            throw new McpException("The onion category requires a configured Tor proxy.");
        }

        return [.. categories.OfType<string>().Distinct()];
    }

    private sealed class McpProgress(IProgress<ProgressNotificationValue>? progress) : ITaskProgress
    {
        public Task ReportAsync(long done, long total, string? stage = null)
        {
            progress?.Report(new ProgressNotificationValue { Progress = done, Total = total, Message = stage });
            return Task.CompletedTask;
        }
    }
}
