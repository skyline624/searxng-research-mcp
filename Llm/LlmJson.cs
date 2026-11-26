using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServeurIa.Core.Llm;

/// <summary>Reads the JSON object a model was asked for with <see cref="LlmOptions.Json"/>.</summary>
public static partial class LlmJson
{
    /// <summary>The instruction added to the conversation by <see cref="LlmOptions.Json"/>.</summary>
    public const string Instruction = "Reply with a single valid JSON object and nothing else: no explanation, no code fence.";

    /// <summary>
    /// The JSON object of an answer, which may be wrapped in a code fence, preceded by a <c>&lt;think&gt;</c> block or surrounded
    /// by text. False when there is none.
    /// </summary>
    public static bool TryParseObject(string answer, out JsonElement value)
    {
        var text = FencePattern().Replace(ThinkPattern().Replace(answer, ""), " ").Trim();
        foreach (var candidate in Candidates(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    value = doc.RootElement.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }

        value = default;
        return false;
    }

    private static IEnumerable<string> Candidates(string text)
    {
        yield return text;
        var open = text.IndexOf('{', StringComparison.Ordinal);
        var close = text.LastIndexOf('}');
        if (open >= 0 && close > open)
        {
            yield return text[open..(close + 1)];
        }
    }

    [GeneratedRegex(@"<think>[\s\S]*?</think>")]
    private static partial Regex ThinkPattern();

    [GeneratedRegex(@"```(?:json)?", RegexOptions.IgnoreCase)]
    private static partial Regex FencePattern();
}
