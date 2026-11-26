using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ServeurIa.Modules.Search.Providers;

/// <summary>
/// SearXNG's JSON API (<c>/search?format=json</c>), on the internal network of the compose profile. The address is trusted
/// configuration, so this client does not use the page fetcher. No identity of the account goes to the engine.
/// </summary>
public sealed class SearxngProvider(IHttpClientFactory httpClients, IOptions<SearchOptions> options, OnionBlocklist blocklist) : ISearchProvider
{
    private static readonly string[] TimeRanges = ["day", "week", "month", "year"];

    public const string HttpClientName = "searxng";

    public string Name => "searxng";

    public bool IsConfigured => options.Value.HasSearxng;

    public async Task<SearchResponse> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new SearchProviderException("Web search is not available: SearXNG is not configured.");
        }

        if (query.TimeRange is { Length: > 0 } range && !TimeRanges.Contains(range))
        {
            throw new ArgumentException($"The time range must be one of {string.Join(", ", TimeRanges)}.", nameof(query));
        }

        var parts = new List<string> { "format=json", $"q={Uri.EscapeDataString(query.Text)}", $"pageno={Math.Max(query.Page, 1)}" };
        if (query.Language is { Length: > 0 } language)
        {
            parts.Add($"language={Uri.EscapeDataString(language)}");
        }

        if (query.Categories is { Count: > 0 } categories)
        {
            parts.Add($"categories={Uri.EscapeDataString(string.Join(',', categories))}");
        }

        if (query.TimeRange is { Length: > 0 } time)
        {
            parts.Add($"time_range={time}");
        }

        if (query.Engines is { Count: > 0 } engines)
        {
            parts.Add($"engines={Uri.EscapeDataString(string.Join(',', engines))}");
        }

        using var http = httpClients.CreateClient(HttpClientName);
        try
        {
            using var response = await http.GetAsync(Endpoint($"search?{string.Join('&', parts)}"), ct);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode == System.Net.HttpStatusCode.Forbidden ? " (is the json format enabled in settings.yml?)" : "";
                throw new SearchProviderException($"SearXNG answered HTTP {(int)response.StatusCode}{hint}.");
            }

            var body = await response.Content.ReadFromJsonAsync<SearxngBody>(ct) ?? new SearxngBody();
            var candidates = (body.Results ?? [])
                .Where(r => Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "onion")
                .ToList();
            // The mandatory blocklist: no .onion result while it cannot be verified, none of its hosts ever.
            candidates.RemoveAll(r => !blocklist.Allows(new Uri(r.Url!, UriKind.Absolute)));
            var results = candidates
                .Take(Math.Clamp(query.MaxResults, 1, 100))
                .Select(r => new SearchResult((r.Title ?? "").Trim(), r.Url!, (r.Content ?? "").Trim(), r.Engine, r.PublishedDate, r.Score))
                .ToList();
            return new SearchResponse(results, Unresponsive(body), Meta(body));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new SearchProviderException($"SearXNG could not be reached: {e.Message}", e);
        }
    }

    public async Task<ProviderHealth> HealthAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return new ProviderHealth(false, false, null, []);
        }

        using var http = httpClients.CreateClient(HttpClientName);
        try
        {
            using var response = await http.GetAsync(Endpoint("config"), ct);
            response.EnsureSuccessStatusCode();
            var config = await response.Content.ReadFromJsonAsync<SearxngConfig>(ct) ?? new SearxngConfig();
            return new ProviderHealth(true, true, null, [.. (config.Engines ?? []).Where(e => e.Enabled && e.Name is not null).Select(e => e.Name!).Order()]);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new ProviderHealth(true, false, e.Message, []);
        }
    }

    private Uri Endpoint(string relative) => new(new Uri(options.Value.SearxngUrl!.TrimEnd('/') + "/"), relative);

    private static List<string> Unresponsive(SearxngBody body) =>
        [.. (body.UnresponsiveEngines ?? []).Where(e => e.Count > 0).Select(e => e.Count > 1 ? $"{e[0]}: {e[1]}" : e[0])];

    /// <summary>The structured feedback of a search; every field is optional, a field the pinned image does not send comes out null.</summary>
    private static QueryMeta Meta(SearxngBody body) => new(
        body.NumberOfResults,
        body.QueryDuration is { } duration ? (int)Math.Round(duration * 1000) : null,
        body.Corrections is { Count: > 0 } corrections ? string.Join(", ", corrections) : null,
        body.Suggestions is { Count: > 0 } suggestions ? suggestions : null);

    private sealed class SearxngBody
    {
        [JsonPropertyName("results")]
        public List<SearxngResult>? Results { get; set; }

        [JsonPropertyName("unresponsive_engines")]
        public List<List<string>>? UnresponsiveEngines { get; set; }

        [JsonPropertyName("number_of_results")]
        public int? NumberOfResults { get; set; }

        [JsonPropertyName("query_duration")]
        public double? QueryDuration { get; set; }

        [JsonPropertyName("corrections")]
        public List<string>? Corrections { get; set; }

        [JsonPropertyName("suggestions")]
        public List<string>? Suggestions { get; set; }
    }

    private sealed class SearxngResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("engine")]
        public string? Engine { get; set; }

        [JsonPropertyName("publishedDate")]
        public string? PublishedDate { get; set; }

        [JsonPropertyName("score")]
        public double Score { get; set; }
    }

    private sealed class SearxngConfig
    {
        [JsonPropertyName("engines")]
        public List<SearxngEngine>? Engines { get; set; }
    }

    private sealed class SearxngEngine
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }
}
