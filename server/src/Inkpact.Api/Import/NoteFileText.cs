using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Inkpact.Api.Search;
using Markdig;

namespace Inkpact.Api.Import;

/// <summary>
/// The text of a note that a page keeps as a file (run 1.17c), for search: its words, as much as is indexed of any page.
/// The page's own text is blank (a file node has no text), and the compactor rewrites that from the document, so this is
/// kept in a column of its own.
/// </summary>
public static partial class NoteFileText
{
    /// <summary>How much of a body is turned into text at most, once the embedded data is out: a few times what is kept.</summary>
    private const int MaxSource = 4_000_000;

    /// <summary>Elements that end a line of text: a word in one is never run into a word in the next.</summary>
    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "p", "div", "html", "body", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption",
        "center", "form", "fieldset", "legend", "caption", "address", "hgroup", "dir", "menu", "thead", "tbody", "tfoot",
        "details", "summary", "dl", "dt", "dd", "li", "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol",
        "table", "blockquote", "pre", "hr", "br",
    };

    /// <summary>Elements whose content is no text of the page.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "template", "noscript", "head", "title", "svg", "math", "iframe", "object",
    };

    /// <summary>
    /// A file put inside the note as a <c>data:</c> address (a picture as base64), which is what makes most notes this large:
    /// no words, and without taking it out first it would be all the text there is.
    /// </summary>
    [GeneratedRegex(@"data:[a-z]+/[a-z0-9.+-]+[;,][^\s)""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataAddress();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"[ \t\f\v ]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@" ?\n[ \n]*\n ?", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();

    [GeneratedRegex(@" ?\n ?", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    public static string Of(string body, bool isHtml)
    {
        var source = ContentRules.Cut(DataAddress().Replace(body, ""), MaxSource).Replace("\0", "");
        string text;
        try
        {
            // Markdown as HTML first, with the converter's own extensions, so that the HTML a Markdown note holds (a clipped
            // table, a div) keeps its words, as it does when the note is content.
            text = TextOfHtml(isHtml ? source : Markdown.ToHtml(source, MarkdownConverter.Pipeline));
        }
        catch (ArgumentException)
        {
            // Markdig refuses to follow elements nested very deep: the words are what search needs, as they stand.
            text = source;
        }

        // The page's own text is cleaned this way too: no control characters, no half surrogate pairs. Then the white space
        // left is made small, so that it does not use up what is kept.
        text = ContentRules.CleanText(text);
        text = Spaces().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        text = LineBreak().Replace(text, "\n").Trim();
        return ContentRules.Cut(text, SearchService.IndexedText);
    }

    /// <summary>
    /// The words of markup as a browser would show them: white space in text is one space, a block ends a line, inline markup
    /// (<c>ex&lt;b&gt;am&lt;/b&gt;ple</c>) leaves the word whole. A walk with a stack of its own, so depth cannot exhaust the
    /// call stack; markup deeper than the converter follows is read without parsing, as the converter does.
    /// </summary>
    private static string TextOfHtml(string html)
    {
        if (ContentRules.MarkupDepth(html) > ContentRules.MaxMarkupDepth)
        {
            return ContentRules.TextOfMarkup(html);
        }

        using var document = new HtmlParser().ParseDocument(html);
        var text = new StringBuilder(html.Length / 2);
        var stack = new Stack<(INode Node, bool Closing)>();
        stack.Push((document.Body ?? (INode)document, false));
        while (stack.Count > 0)
        {
            var (node, closing) = stack.Pop();
            if (node is IText textNode)
            {
                text.Append(WhiteSpace().Replace(textNode.Data, " "));
                continue;
            }

            var name = (node as IElement)?.LocalName;
            if (name is not null && Skipped.Contains(name))
            {
                continue;
            }

            var block = name is not null && Blocks.Contains(name);
            if (closing)
            {
                if (block)
                {
                    text.Append('\n');
                }

                continue;
            }

            if (block)
            {
                text.Append('\n');
            }

            stack.Push((node, true));
            for (var child = node.LastChild; child is not null; child = child.PreviousSibling)
            {
                stack.Push((child, false));
            }
        }

        return text.ToString();
    }
}
