using System.Text.Json;
using Microsoft.Extensions.Options;
using ServeurIa.Modules.Search;
using ServeurIa.Modules.Search.Agent;
using ServeurIa.Modules.Search.Pages;
using ServeurIa.Modules.Search.Providers;

namespace ServeurIa.Tests.Search;

public sealed class ResearchCandidatePoolTests
{
    [Theory]
    [InlineData("speed", 8, 4)]
    [InlineData("balanced", 16, 8)]
    [InlineData("quality", 24, 12)]
    public async Task ExpandedCandidatesAreRankedTogetherBeforeTheModeLimitsApply(string mode, int readBudget, int sourceBudget)
    {
        var pages = new FakePages();
        var article = SearchTestKit.Article("Paris", "Paris is the capital of France and is located on the Seine. The city has more than two million residents within its municipal boundaries.");
        var results = new Dictionary<(string Text, int Page, bool Ahmia), SearchResponse>();
        foreach (var (query, number) in new[] { ("Paris capital", 1), ("Paris population", 2) })
        {
            foreach (var page in new[] { 1, 2, 3 })
            {
                foreach (var ahmia in new[] { false, true })
                {
                    var items = Enumerable.Range(1, ahmia ? 10 : 14).Select(i =>
                    {
                        // Ahmia can also supply public URLs and these still belong in its share.
                        var prefix = ahmia ? "ahmia" : "public";
                        var url = $"https://{prefix}-{number}-{page}-{i}.example/paris";
                        pages.Html(url, article);
                        return FakeProvider.Result(url, url, "Paris facts");
                    }).ToList();
                    results[(query, page, ahmia)] = new SearchResponse(items, []);
                }
            }
        }

        var provider = new FakeProvider(q => results[(q.Text, q.Page, q.Categories is ["onions"])]);
        string? chosen = null;
        var llm = new ScriptedLlm((messages, options) =>
        {
            if (options?.Json != true)
            {
                return "Paris is the capital of France [1].";
            }

            if (!messages[0].text.StartsWith("Rank search candidates", StringComparison.Ordinal))
            {
                return """{"language":"en","queries":["Paris capital","Paris population"]}""";
            }

            using var input = JsonDocument.Parse(messages[^1].text);
            var candidates = input.RootElement.GetProperty("candidates").EnumerateArray().ToList();
            Assert.Equal(100, candidates.Count);
            Assert.True(candidates.Count(c => c.GetProperty("domain").GetString()!.StartsWith("ahmia-", StringComparison.Ordinal)) >= 20);
            var last = candidates[^1];
            chosen = last.GetProperty("title").GetString();
            return JsonSerializer.Serialize(new { ids = new[] { last.GetProperty("id").GetInt32() } });
        });
        var agent = new ResearchAgent(llm, provider, new PageService(pages, new FakeRenderer(available: false), SearchTestKit.Settings()), Options.Create(new SearchOptions()));

        var result = await agent.RunAsync(Guid.NewGuid(), new ResearchRequest("What is the capital of France?", mode, ["web", "onion"], null, "en"),
            new RecordingProgress(), CancellationToken.None);

        Assert.Contains(provider.Queries, q => q.Page == 3);
        var fetched = pages.Fetches.Select(f => f.Url.AbsoluteUri).ToList();
        Assert.Equal(readBudget, fetched.Count);
        Assert.Contains(chosen, fetched);
        Assert.Equal(sourceBudget, result.Sources.Count);
        Assert.All(result.Sources, source => Assert.Contains(source.Url, fetched));
    }
}
