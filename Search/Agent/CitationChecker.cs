using System.Text.RegularExpressions;

namespace ServeurIa.Modules.Search.Agent;

/// <summary>
/// Checks the <c>[n]</c> citations of an answer against the sources it was given: an index that is not a source is removed (the model
/// invented it), and a paragraph with no valid citation is reported. The check is code, not another model call.
/// </summary>
public static partial class CitationChecker
{
    public sealed record Checked(string Text, IReadOnlyList<string> Warnings, IReadOnlySet<int> Cited);

    /// <summary>Paragraphs shorter than this are headings, labels or one-liners: no source is expected.</summary>
    private const int MinCitedParagraphChars = 60;

    public static Checked Check(string answer, int sourceCount)
    {
        var cited = new HashSet<int>();
        var warnings = new List<string>();
        var removed = new SortedSet<int>();
        var text = CitationPattern().Replace(answer, match =>
        {
            var numbers = match.Groups[2].Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
            var valid = numbers.Where(n => n >= 1 && n <= sourceCount).Distinct().ToList();
            foreach (var n in numbers.Except(valid))
            {
                removed.Add(n);
            }

            cited.UnionWith(valid);
            return valid.Count == 0 ? "" : $"{match.Groups[1].Value}[{string.Join(", ", valid)}]";
        });

        if (removed.Count > 0)
        {
            warnings.Add($"Citations of unknown sources were removed: {string.Join(", ", removed.Select(n => $"[{n}]"))}.");
        }

        if (cited.Count == 0)
        {
            warnings.Add("The answer cites no source.");
        }
        else
        {
            var number = 0;
            foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                number++;
                var trimmed = paragraph.TrimStart();
                if (trimmed.StartsWith('#') || trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.Length < MinCitedParagraphChars)
                {
                    continue;
                }

                if (!CitationPattern().IsMatch(paragraph))
                {
                    warnings.Add($"Paragraph {number} has no source.");
                }
            }
        }

        return new Checked(text.Trim(), warnings, cited);
    }

    // The space before a citation goes with it when the citation is removed.
    [GeneratedRegex(@"( ?)\[(\d{1,4}(?:\s*[,;]\s*\d{1,4})*)\]")]
    private static partial Regex CitationPattern();
}
