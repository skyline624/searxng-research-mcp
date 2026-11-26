using System.Text.RegularExpressions;

namespace ServeurIa.Infrastructure.Web;

/// <summary>
/// The rules of a robots.txt for one crawler (RFC 9309): the groups that name it, else the <c>*</c> group; the longest matching
/// rule wins and <c>Allow</c> beats <c>Disallow</c> on a tie; <c>*</c> and a final <c>$</c> are wildcards.
/// </summary>
public sealed class RobotsRules
{
    private readonly List<(bool Allow, Regex Pattern, int Length)> _rules;

    private RobotsRules(List<(bool, Regex, int)> rules) => _rules = rules;

    public static RobotsRules AllowAll { get; } = new([]);

    public static RobotsRules Parse(string text, string agent)
    {
        var groups = new List<(List<string> Agents, List<(bool Allow, string Path)> Rules)>();
        List<string>? agents = null;
        var collecting = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (!collecting)
                {
                    agents = [];
                    groups.Add((agents, []));
                }

                agents!.Add(value.ToLowerInvariant());
                collecting = true;
            }
            else if (field is "allow" or "disallow" && groups.Count > 0)
            {
                collecting = false;
                groups[^1].Rules.Add((field == "allow", value));
            }
            else
            {
                collecting = false;
            }
        }

        var token = agent.ToLowerInvariant();
        var named = groups.Where(g => g.Agents.Any(a => a != "*" && a.Length > 0 && token.Contains(a, StringComparison.Ordinal))).ToList();
        var chosen = named.Count > 0 ? named : [.. groups.Where(g => g.Agents.Contains("*"))];
        var rules = new List<(bool, Regex, int)>();
        foreach (var (allow, path) in chosen.SelectMany(g => g.Rules))
        {
            // An empty Disallow allows everything; an empty Allow says nothing.
            if (path.Length > 0)
            {
                rules.Add((allow, ToRegex(path), path.Length));
            }
        }

        return new RobotsRules(rules);
    }

    /// <summary>True when the crawler may fetch this path (with its query).</summary>
    public bool IsAllowed(string pathAndQuery)
    {
        var best = (Allow: true, Length: -1);
        foreach (var (allow, pattern, length) in _rules)
        {
            if (pattern.IsMatch(pathAndQuery) && (length > best.Length || (length == best.Length && allow)))
            {
                best = (allow, length);
            }
        }

        return best.Allow;
    }

    private static Regex ToRegex(string path)
    {
        var anchored = path.EndsWith('$');
        var body = anchored ? path[..^1] : path;
        var pattern = "^" + Regex.Escape(body).Replace("\\*", ".*", StringComparison.Ordinal) + (anchored ? "$" : "");
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
