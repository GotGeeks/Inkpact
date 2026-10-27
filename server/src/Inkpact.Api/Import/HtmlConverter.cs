using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Inkpact.Api.Import;

/// <summary>
/// HTML to the editor's blocks: for HTML notes (mostly from the web clipper) and HTML inside Markdown. The parse is a
/// browser's (AngleSharp, no scripts, no loading); what comes out is built from the editor's own node types, so the
/// allow-list is not a filter that can miss something: whatever the converter does not understand keeps its text and
/// loses its element, and the report says so.
/// </summary>
internal sealed class HtmlConverter(ConversionContext context) : IDisposable
{
    /// <summary>Elements that begin and end a paragraph, though the editor has no node for them (or none that the converter makes).</summary>
    private static readonly HashSet<string> BreakingElements = new(StringComparer.Ordinal)
    {
        "p", "div", "html", "body", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption",
        "center", "form", "fieldset", "legend", "caption", "address", "hgroup", "dir", "menu", "thead", "tbody", "tfoot",
        "details", "summary", "dl", "dt", "dd", "li", "tr", "td", "th",
    };

    private readonly HtmlParser _parser = new();
    private IDocument? _fragmentContext;

    private ConversionNotes Notes => context.Notes;

    /// <param name="html">The markup.</param>
    /// <param name="blocks">How deep in blocks the markup already is (HTML in a Markdown list).</param>
    public List<PmNode> Convert(string html, int blocks = 0)
    {
        if (ContentRules.MarkupDepth(html) > ContentRules.MaxMarkupDepth)
        {
            // The parser's time grows with the square of the depth, and nobody writes a page this deep: the text stays,
            // as plain paragraphs, and nothing of the markup is followed.
            Notes.Add(NoteCodes.NestingFlattened, "html");
            return Shapes.PlainParagraphs(ContentRules.TextOfMarkup(html));
        }

        var sink = new BlockSink(collapseWhiteSpace: true, Notes);
        foreach (var node in Parse(html))
        {
            Walk(node, sink, new TextMarks(), blocks, 0);
        }

        return sink.Finish();
    }

    /// <summary>The element one tag makes on its own (an <c>img</c> in the middle of Markdown), to read its attributes.</summary>
    public IElement? ParseElement(string tag) => Parse(tag).OfType<IElement>().FirstOrDefault();

    /// <summary>The nodes of <paramref name="html"/>, parsed as the content of a page's body.</summary>
    private INodeList Parse(string html)
    {
        _fragmentContext ??= _parser.ParseDocument("");
        return _parser.ParseFragment(html, _fragmentContext.Body!);
    }

    public void Dispose() => _fragmentContext?.Dispose();

    private void Walk(INode node, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        switch (node)
        {
            case IText text:
                sink.AddText(text.Data, marks);
                break;
            case IElement element:
                WalkElement(element, sink, marks, blocks, depth);
                break;
        }
    }

    private void WalkChildren(INode parent, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        foreach (var child in parent.ChildNodes)
        {
            Walk(child, sink, marks, blocks, depth + 1);
        }
    }

    private void WalkElement(IElement element, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        var tag = element.LocalName;

        if (depth > ContentRules.MaxTreeDepth)
        {
            // Deeper than anyone writes: the text stays, the structure does not, and nothing here recurses any further.
            Notes.Add(NoteCodes.NestingFlattened, "html");
            sink.AddText(TextOf(element), marks);
            return;
        }

        if (ContentRules.RemovedElements.Contains(tag))
        {
            if (!ContentRules.SilentlyRemovedElements.Contains(tag))
            {
                Notes.Add(NoteCodes.RemovedElement, tag);
            }

            return;
        }

        switch (tag)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var heading = Collect(element, marks, blocks, depth);
                sink.AddBlocks(Shapes.Textblock("heading", Shapes.Attributes("level", tag[1] - '0'), heading));
                break;

            case "blockquote":
                Quote(element, sink, marks, blocks, depth);
                break;

            case "ul" or "ol":
                List(element, sink, marks, blocks, depth);
                break;

            case "pre":
                sink.AddBlock(CodeBlock(element));
                break;

            case "table":
                Table(element, sink, marks, blocks, depth);
                break;

            case "hr":
                sink.AddBlock(PmNode.Element("horizontalRule"));
                break;

            case "br":
                sink.AddInline(PmNode.Element("hardBreak"));
                break;

            case "img":
                Image(element, sink, marks);
                break;

            case "a":
                Link(element, sink, marks, blocks, depth);
                break;

            case "input":
                // A checkbox is read by its list item; any other control has nothing to say.
                break;

            case "strong" or "b":
                WalkChildren(element, sink, Styled(element, marks with { Bold = true }), blocks, depth);
                break;

            case "em" or "i":
                WalkChildren(element, sink, Styled(element, marks with { Italic = true }), blocks, depth);
                break;

            case "u":
                WalkChildren(element, sink, Styled(element, marks with { Underline = true }), blocks, depth);
                break;

            case "s" or "del" or "strike":
                WalkChildren(element, sink, Styled(element, marks with { Strike = true }), blocks, depth);
                break;

            case "code" or "kbd" or "samp" or "tt":
                WalkChildren(element, sink, Styled(element, marks with { Code = true }), blocks, depth);
                break;

            default:
                var breaks = BreakingElements.Contains(tag);
                if (tag is "dl" or "dt" or "dd")
                {
                    Notes.Add(NoteCodes.DefinitionList);
                }
                else if (tag is "details" or "summary"
                    || !(ContentRules.AllowedTags.Contains(tag) || ContentRules.IsLayoutElement(tag) || breaks))
                {
                    Notes.Add(NoteCodes.UnsupportedElement, tag);
                }

                if (breaks)
                {
                    sink.EndParagraph();
                }

                WalkChildren(element, sink, Styled(element, marks), blocks, depth);
                if (breaks)
                {
                    sink.EndParagraph();
                }

                break;
        }
    }

    /// <summary>The blocks that an element's content makes, in a sink of their own.</summary>
    private List<PmNode> Collect(IElement element, TextMarks marks, int blocks, int depth)
    {
        var sink = new BlockSink(collapseWhiteSpace: true, Notes);
        WalkChildren(element, sink, Styled(element, marks), blocks, depth);
        return sink.Finish();
    }

    private void Quote(IElement element, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        var content = Collect(element, marks, blocks + 1, depth);
        if (blocks >= ContentRules.MaxBlockNesting)
        {
            Notes.Add(NoteCodes.NestingFlattened, "blockquote");
            sink.AddBlocks(content);
        }
        else if (content.Count > 0)
        {
            sink.AddBlock(PmNode.Element("blockquote", content));
        }
    }

    private void List(IElement list, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        var items = new List<RawItem>();
        foreach (var child in list.ChildNodes)
        {
            if (child is IElement { LocalName: "li" } li)
            {
                var checkbox = FindCheckbox(li);
                var isTask = checkbox is not null || li.GetAttribute("data-type") == "taskItem";
                var isChecked = isTask && (checkbox?.HasAttribute("checked") == true || li.GetAttribute("data-checked") == "true");
                items.Add(new RawItem(isTask, isChecked, Collect(li, marks, blocks + 1, depth + 1)));
            }
            else if (child is IElement || (child is IText text && !string.IsNullOrWhiteSpace(text.Data)))
            {
                // Not valid HTML, but notes have it: whatever stands between the items is an item of its own.
                var itemSink = new BlockSink(collapseWhiteSpace: true, Notes);
                Walk(child, itemSink, marks, blocks + 1, depth + 1);
                var content = itemSink.Finish();
                if (content.Count > 0)
                {
                    items.Add(new RawItem(false, false, content));
                }
            }
        }

        var ordered = list.LocalName == "ol";
        sink.AddBlocks(Shapes.Lists(items, ordered, ordered ? Shapes.ListStart(list.GetAttribute("start")) : 1, blocks, Notes));
    }

    private static IElement? FindCheckbox(IElement item)
    {
        static bool IsCheckbox(IElement element) =>
            element.LocalName == "input" && string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase);

        foreach (var child in item.Children)
        {
            if (IsCheckbox(child))
            {
                return child;
            }

            if (child.LocalName is "label" or "div" or "p" or "span")
            {
                var inner = child.Children.FirstOrDefault(IsCheckbox);
                if (inner is not null)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private void Table(IElement table, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        // Rows without cells are kept: the row spans count them.
        var rows = RowsOf(table)
            .Select(row => (IReadOnlyList<RawCell>)
            [
                .. row.Children.Where(c => c.LocalName is "td" or "th").Select(cell => new RawCell(
                    cell.LocalName == "th",
                    Collect(cell, marks, blocks + 1, depth + 2),
                    Span(cell.GetAttribute("colspan")),
                    Span(cell.GetAttribute("rowspan")))),
            ])
            .ToList();

        sink.AddBlocks(Shapes.Table(rows, blocks, Notes));
    }

    private static IEnumerable<IElement> RowsOf(IElement table)
    {
        foreach (var child in table.Children)
        {
            if (child.LocalName == "tr")
            {
                yield return child;
            }
            else if (child.LocalName is "thead" or "tbody" or "tfoot")
            {
                foreach (var row in child.Children.Where(c => c.LocalName == "tr"))
                {
                    yield return row;
                }
            }
        }
    }

    private static int Span(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var span) && span > 0 ? span : 1;

    private PmNode CodeBlock(IElement pre)
    {
        var language = ContentRules.Language(LanguageClass(pre) ?? (pre.Children.Length == 1 ? LanguageClass(pre.Children[0]) : null));
        var text = ContentRules.CleanText(TextOf(pre)).TrimEnd('\n');
        var attributes = language is null ? null : Shapes.Attributes("language", language);
        return PmNode.Element("codeBlock", text.Length > 0 ? [PmNode.TextNode(text)] : null, attributes);
    }

    private static string? LanguageClass(IElement element)
    {
        foreach (var name in (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var prefix in (string[])["language-", "lang-"])
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return name[prefix.Length..];
                }
            }
        }

        return null;
    }

    private void Link(IElement element, BlockSink sink, TextMarks marks, int blocks, int depth)
    {
        if (context.ResolveLink(element.GetAttribute("href"), () => TextOf(element), out var href) is { } file)
        {
            sink.AddInline(file);
            return;
        }

        // A link inside a link is not a thing: the outer one stays.
        var inner = href is not null && marks.Href is null ? marks with { Href = href } : marks;
        WalkChildren(element, sink, Styled(element, inner), blocks, depth);
    }

    private void Image(IElement element, BlockSink sink, TextMarks marks) =>
        context.AddImage(sink, element.GetAttribute("src"), element.GetAttribute("alt") ?? "", marks);

    /// <summary>
    /// Formatting written as a style (Google Docs and Word write <c>font-weight</c> and <c>text-decoration</c> on a span)
    /// is read for the marks the editor has; everything else a style says is left behind.
    /// </summary>
    private static TextMarks Styled(IElement element, TextMarks marks)
    {
        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return marks;
        }

        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var property = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim().ToLowerInvariant();
            switch (property)
            {
                case "font-weight":
                    marks = marks with
                    {
                        Bold = value is "bold" or "bolder" || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) && weight >= 600),
                    };
                    break;
                case "font-style":
                    marks = marks with { Italic = value is "italic" or "oblique" };
                    break;
                case "text-decoration" or "text-decoration-line":
                    marks = marks with
                    {
                        Underline = marks.Underline || value.Contains("underline", StringComparison.Ordinal),
                        Strike = marks.Strike || value.Contains("line-through", StringComparison.Ordinal),
                    };
                    break;
            }
        }

        return marks;
    }

    /// <summary>The text of a node and everything in it, with a line break for each <c>br</c>; no recursion, so no tree is too deep for it.</summary>
    private static string TextOf(INode root)
    {
        var text = new StringBuilder();
        var stack = new Stack<INode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case IText data:
                    text.Append(data.Data);
                    break;
                case IElement { LocalName: "br" }:
                    text.Append('\n');
                    break;
                case IElement element when ContentRules.RemovedElements.Contains(element.LocalName):
                    break;
                default:
                    for (var i = node.ChildNodes.Length - 1; i >= 0; i--)
                    {
                        stack.Push(node.ChildNodes[i]);
                    }

                    break;
            }
        }

        return text.ToString();
    }
}
