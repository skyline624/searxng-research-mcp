using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ServeurIa.Modules.Search.Pages;

/// <summary>A page reduced to its readable content.</summary>
public sealed record CleanedPage(string Title, string? Author, string? Language, DateTimeOffset? Published, string Markdown, int? PageCount = null);

/// <summary>
/// HTML to readable markdown: AngleSharp parses, hidden and non-content elements are dropped (which also removes text a page hides
/// from people but not from a model), SmartReader (a port of Mozilla's Readability) picks the main content, and a small walker
/// writes it as markdown. PDF text comes from PdfPig. Everything here is pure: no network.
/// </summary>
public static partial class PageCleaner
{
    private const int MaxPdfPages = 60;

    private static readonly string[] Dropped = ["script", "style", "noscript", "template", "iframe", "object", "embed", "svg", "canvas", "form", "dialog"];

    public static CleanedPage FromHtml(string html, Uri url)
    {
        var document = new HtmlParser().ParseDocument(html);
        Prune(document);
        var language = document.DocumentElement?.GetAttribute("lang") is { Length: > 0 } lang ? lang.Split('-')[0].ToLowerInvariant() : null;
        var pruned = document.DocumentElement?.OuterHtml ?? "";

        var article = SmartReader.Reader.ParseArticle(url.ToString(), pruned);
        string markdown;
        if (article.IsReadable && !string.IsNullOrWhiteSpace(article.Content))
        {
            var body = new HtmlParser().ParseDocument(article.Content).Body;
            markdown = ToMarkdown(body);
        }
        else
        {
            // Not an article (a listing, a short page): the whole body, without the page furniture.
            foreach (var junk in document.QuerySelectorAll("nav, header, footer, aside").ToList())
            {
                junk.Remove();
            }

            markdown = ToMarkdown(document.Body);
        }

        var title = !string.IsNullOrWhiteSpace(article.Title) ? article.Title : document.Title ?? "";
        DateTimeOffset? published = article.PublicationDate is { } date && date != DateTime.MinValue ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
        return new CleanedPage(Collapse(title), string.IsNullOrWhiteSpace(article.Author) ? null : Collapse(article.Author), language ?? article.Language?.Split('-')[0].ToLowerInvariant(), published, markdown);
    }

    /// <summary>The text of a PDF, page by page in reading order, at most <see cref="MaxPdfPages"/> pages.</summary>
    public static CleanedPage FromPdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages().Take(MaxPdfPages))
            {
                var pageText = ContentOrderTextExtractor.GetText(page).Trim();
                if (pageText.Length > 0)
                {
                    text.Append(pageText).Append("\n\n");
                }
            }

            return new CleanedPage(Collapse(pdf.Information.Title ?? ""), pdf.Information.Author is { Length: > 0 } a ? Collapse(a) : null, null, null, Tidy(text.ToString()), pdf.NumberOfPages);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or NotSupportedException || e.GetType().Namespace?.StartsWith("UglyToad", StringComparison.Ordinal) == true)
        {
            throw new SearchException(422, $"The PDF could not be read: {e.Message}");
        }
    }

    public static CleanedPage FromText(string text, Uri url) =>
        new(url.Host, null, null, null, Tidy(text));

    /// <summary>Plain text of markdown (for lengths and ranking): links keep their label, markup goes.</summary>
    public static string ToPlain(string markdown) =>
        Collapse(LinkPattern().Replace(markdown, "$1").Replace("#", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal));

    /// <summary>The markdown without its markup, paragraphs kept: links keep their label, headings and emphasis lose their signs.</summary>
    public static string ToText(string markdown) =>
        HeadingMarks().Replace(LinkPattern().Replace(markdown, "$1"), "").Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);

    private static void Prune(IDocument document)
    {
        foreach (var element in document.QuerySelectorAll(string.Join(',', Dropped)).ToList())
        {
            element.Remove();
        }

        foreach (var hidden in document.All.Where(IsHidden).ToList())
        {
            hidden.Remove();
        }

        // Comments are not content, and are a classic place to hide instructions.
        var walker = document.CreateTreeWalker(document, FilterSettings.Comment);
        var comments = new List<INode>();
        while (walker.ToNext() is { } node)
        {
            comments.Add(node);
        }

        foreach (var comment in comments)
        {
            comment.Parent?.RemoveChild(comment);
        }
    }

    private static bool IsHidden(IElement e)
    {
        if (e.HasAttribute("hidden") || e.GetAttribute("aria-hidden") == "true")
        {
            return true;
        }

        var style = e.GetAttribute("style");
        return style is { Length: > 0 } && HiddenStyle().IsMatch(style);
    }

    // ---- markdown ----

    private static string ToMarkdown(IElement? root)
    {
        if (root is null)
        {
            return "";
        }

        var output = new StringBuilder();
        Block(root, output, 0);
        return Tidy(output.ToString());
    }

    private static void Block(INode node, StringBuilder output, int listDepth)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    output.Append(Collapse(text.Data, keepEdges: true));
                    break;
                case IElement e:
                    Element(e, output, listDepth);
                    break;
            }
        }
    }

    private static void Element(IElement e, StringBuilder output, int listDepth)
    {
        var name = e.LocalName;
        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                output.Append("\n\n").Append('#', name[1] - '0').Append(' ').Append(Collapse(Inline(e))).Append("\n\n");
                break;
            case "p" or "div" or "section" or "article" or "main" or "figure" or "figcaption" or "header" or "footer":
                output.Append("\n\n");
                Block(e, output, listDepth);
                output.Append("\n\n");
                break;
            case "br":
                output.Append('\n');
                break;
            case "hr":
                output.Append("\n\n---\n\n");
                break;
            case "ul" or "ol":
                output.Append('\n');
                var index = 0;
                foreach (var item in e.Children.Where(c => c.LocalName == "li"))
                {
                    index++;
                    output.Append('\n').Append(' ', listDepth * 2).Append(name == "ol" ? $"{index}. " : "- ");
                    var nested = new StringBuilder();
                    Block(item, nested, listDepth + 1);
                    output.Append(Collapse(nested.ToString().Replace("\n", " ", StringComparison.Ordinal)));
                }

                output.Append("\n\n");
                break;
            case "pre":
                output.Append("\n\n```\n").Append(e.TextContent.Trim('\n')).Append("\n```\n\n");
                break;
            case "blockquote":
                var quoted = new StringBuilder();
                Block(e, quoted, listDepth);
                output.Append("\n\n").Append(string.Join('\n', Tidy(quoted.ToString()).Split('\n').Select(l => "> " + l))).Append("\n\n");
                break;
            case "table":
                Table(e, output);
                break;
            case "img":
                break;
            default:
                // Inline elements (a, em, strong, span...) and unknown containers.
                output.Append(InlineNode(e));
                break;
        }
    }

    private static string Inline(INode node) => string.Concat(node.ChildNodes.Select(InlineNode));

    private static string InlineNode(INode node)
    {
        switch (node)
        {
            case IText t:
                return t.Data;
            case IElement { LocalName: "a" } a:
                var label = Collapse(Inline(a));
                var href = a.GetAttribute("href");
                return label.Length > 0 && href is { Length: > 0 } && !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ? $"[{label}]({href})" : label;
            case IElement { LocalName: "strong" or "b" } strong:
                return $"**{Collapse(Inline(strong))}**";
            case IElement { LocalName: "em" or "i" } em:
                return $"*{Collapse(Inline(em))}*";
            case IElement { LocalName: "code" } code:
                return $"`{code.TextContent}`";
            case IElement { LocalName: "br" }:
                return " ";
            case IElement { LocalName: "img" }:
                return "";
            case IElement e:
                return Inline(e);
            default:
                return "";
        }
    }

    private static void Table(IElement table, StringBuilder output)
    {
        output.Append("\n\n");
        var first = true;
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.Children.Where(c => c.LocalName is "td" or "th").Select(c => Collapse(c.TextContent).Replace("|", "/", StringComparison.Ordinal)).ToList();
            if (cells.Count == 0)
            {
                continue;
            }

            output.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (first)
            {
                output.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cells.Count))).Append('\n');
                first = false;
            }
        }

        output.Append('\n');
    }

    private static string Collapse(string text, bool keepEdges = false)
    {
        var collapsed = Whitespace().Replace(text, " ");
        return keepEdges ? collapsed : collapsed.Trim();
    }

    /// <summary>One blank line between blocks, no trailing spaces, no stray control characters.</summary>
    private static string Tidy(string text)
    {
        var cleaned = new string([.. text.Replace("\r", "", StringComparison.Ordinal).Where(c => c is '\n' or '\t' || !char.IsControl(c))]);
        var lines = cleaned.Split('\n').Select(l => TrailingSpace().Replace(l, ""));
        return BlankRuns().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[ \t]+$")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"^#{1,6} ", RegexOptions.Multiline)]
    private static partial Regex HeadingMarks();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"display\s*:\s*none|visibility\s*:\s*hidden|font-size\s*:\s*0(?![.\d])|opacity\s*:\s*0(?![.\d])", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenStyle();
}
