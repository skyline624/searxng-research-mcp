using System.Globalization;
using System.Text;

namespace ServeurIa.Modules.Search.Ranking;

/// <summary>
/// Words for ranking: lower case, accents folded (so "été" and "ete" meet), stop words of French and English dropped, and a very light
/// stemmer (plural and a few suffixes) so "chats" and "chat", "running" and "run" count as one term.
/// </summary>
public static class Terms
{
    private static readonly HashSet<string> Stop = new(
        (
            "a an and are as at be by for from has have in is it its of on or that the this to was were will with " +
            "au aux avec ce ces dans de des du elle en et eux il ils je la le les leur lui ma mais me meme mes moi mon ne nos notre nous on ou par pas pour qu que qui sa se ses son sur ta te tes toi ton tu un une vos votre vous " +
            "est sont etait ete etre avoir comme plus tres"
        ).Split(' '),
        StringComparer.Ordinal);

    private static readonly string[] Suffixes = ["ements", "ement", "ations", "ation", "ings", "ing", "eaux", "ies", "es", "ed", "s", "x"];

    public static List<string> Tokenize(string text)
    {
        var terms = new List<string>();
        var word = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else
            {
                Flush(word, terms);
            }
        }

        Flush(word, terms);
        return terms;
    }

    private static void Flush(StringBuilder word, List<string> terms)
    {
        if (word.Length == 0)
        {
            return;
        }

        var text = word.ToString();
        word.Clear();
        if (text.Length > 1 && !Stop.Contains(text))
        {
            terms.Add(Stem(text));
        }
    }

    private static string Stem(string word)
    {
        foreach (var suffix in Suffixes)
        {
            if (word.Length >= suffix.Length + 3 && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }
}

/// <summary>Okapi BM25: how well each text answers a query, from term frequency, rarity across the texts and length.</summary>
public static class Bm25
{
    public const double K1 = 1.5;
    public const double B = 0.75;

    /// <summary>One score per text, in order; 0 for a text that shares no term with the query.</summary>
    public static double[] Score(string query, IReadOnlyList<string> texts)
    {
        var queryTerms = Terms.Tokenize(query).Distinct().ToList();
        var docs = texts.Select(Terms.Tokenize).ToList();
        var scores = new double[docs.Count];
        if (docs.Count == 0 || queryTerms.Count == 0)
        {
            return scores;
        }

        var average = Math.Max(docs.Average(d => d.Count), 1);
        var counts = docs.Select(d => d.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count())).ToList();
        foreach (var term in queryTerms)
        {
            var holding = counts.Count(c => c.ContainsKey(term));
            if (holding == 0)
            {
                continue;
            }

            // The "+1" keeps the weight positive for a term that most texts contain.
            var idf = Math.Log(1 + ((docs.Count - holding + 0.5) / (holding + 0.5)));
            for (var i = 0; i < docs.Count; i++)
            {
                if (!counts[i].TryGetValue(term, out var tf))
                {
                    continue;
                }

                scores[i] += idf * (tf * (K1 + 1)) / (tf + (K1 * (1 - B + (B * docs[i].Count / average))));
            }
        }

        return scores;
    }
}

/// <summary>Cuts a page into passages of about <c>target</c> characters, along its paragraphs.</summary>
public static class Passages
{
    public static List<string> Split(string markdown, int target = 900)
    {
        var passages = new List<string>();
        var current = new StringBuilder();
        foreach (var block in markdown.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A block longer than the target (a pasted table, one long paragraph) is cut on its own.
            foreach (var piece in block.Length > target * 2 ? Chop(block, target) : [block])
            {
                if (current.Length > 0 && current.Length + piece.Length > target)
                {
                    passages.Add(current.ToString().Trim());
                    current.Clear();
                }

                current.Append(piece).Append("\n\n");
            }
        }

        if (current.Length > 0)
        {
            passages.Add(current.ToString().Trim());
        }

        return [.. passages.Where(p => p.Length > 0)];
    }

    private static IEnumerable<string> Chop(string text, int target)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(target, text.Length - start);
            // Cut at a space when there is one close, not in the middle of a word.
            var space = start + length < text.Length ? text.LastIndexOf(' ', start + length - 1, Math.Min(length, 120)) : -1;
            if (space > start)
            {
                length = space - start;
            }

            yield return text.Substring(start, length).Trim();
            start += length;
        }
    }
}
