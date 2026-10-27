using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Inkpact.Api.Import;

/// <summary>
/// Joplin's Markdown to the editor's blocks. Markdig parses (CommonMark plus the extensions Joplin has: tables, task
/// lists, strikethrough, autolinks, footnotes, definition lists, math); this walks the result and builds nodes of the
/// editor's schema. HTML inside the Markdown goes through <see cref="HtmlConverter"/>. What the schema cannot hold keeps
/// its text, and the report says what it was.
/// </summary>
internal sealed partial class MarkdownConverter(ConversionContext context, HtmlConverter html)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseDefinitionLists()
        .UseMathematics()
        .Build();

    /// <summary>Block nesting past which the editor gets no more levels, and how far the walk follows at all.</summary>
    private const int HardDepth = 150;

    private ConversionNotes Notes => context.Notes;

    [GeneratedRegex(@"^<\s*/?\s*(?<name>[a-zA-Z][a-zA-Z0-9]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TagName();

    public List<PmNode> Convert(string markdown)
    {
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(markdown, Pipeline);
        }
        catch (ArgumentException)
        {
            // Markdig refuses to follow elements nested this deep (a list in a list in a list, a thousand times over).
            // Nobody wrote that on purpose; the text stays, as plain paragraphs.
            Notes.Add(NoteCodes.NestingFlattened, "markdown");
            return Shapes.PlainParagraphs(markdown);
        }

        var sink = new BlockSink(collapseWhiteSpace: false, Notes);
        Blocks(document, sink, 0);
        return sink.Finish();
    }

    private void Blocks(ContainerBlock container, BlockSink sink, int depth)
    {
        foreach (var block in container)
        {
            Block(block, sink, depth);
        }
    }

    private List<PmNode> Collect(ContainerBlock container, int depth, string? prefix = null)
    {
        var sink = new BlockSink(collapseWhiteSpace: false, Notes);
        if (prefix is not null)
        {
            sink.AddText(prefix, new TextMarks());
        }

        Blocks(container, sink, depth);
        return sink.Finish();
    }

    private void Block(Block block, BlockSink sink, int depth)
    {
        if (depth > HardDepth)
        {
            // Nested deeper than anything written by a person: the text stays, and the walk goes no deeper.
            Notes.Add(NoteCodes.NestingFlattened, "markdown");
            foreach (var leaf in block.Descendants<LeafBlock>().Take(10_000))
            {
                sink.AddBlock(Shapes.Paragraph(PmNode.TextNode(ContentRules.CleanText(leaf.Lines.ToString()))));
            }

            return;
        }

        switch (block)
        {
            case HeadingBlock heading:
                var content = new BlockSink(collapseWhiteSpace: false, Notes);
                InlinesOf(heading.Inline, content);
                sink.AddBlocks(Shapes.Textblock("heading", Shapes.Attributes("level", Math.Clamp(heading.Level, 1, 6)), content.Finish()));
                break;

            case ParagraphBlock paragraph:
                InlinesOf(paragraph.Inline, sink);
                sink.EndParagraph();
                break;

            case QuoteBlock quote:
                var quoted = Collect(quote, depth + 1);
                if (depth >= ContentRules.MaxBlockNesting)
                {
                    Notes.Add(NoteCodes.NestingFlattened, "blockquote");
                    sink.AddBlocks(quoted);
                }
                else if (quoted.Count > 0)
                {
                    sink.AddBlock(PmNode.Element("blockquote", quoted));
                }

                break;

            case ListBlock list:
                List(list, sink, depth);
                break;

            case MathBlock math:
                // The editor has no formulas: the source stays, as a block of its own.
                Notes.Add(NoteCodes.Math);
                sink.AddBlock(CodeBlock(math.Lines.ToString(), "latex"));
                break;

            case FencedCodeBlock fenced:
                var language = ContentRules.Language(fenced.Info);
                if (string.Equals(language, "mermaid", StringComparison.OrdinalIgnoreCase))
                {
                    Notes.Add(NoteCodes.Mermaid);
                }

                sink.AddBlock(CodeBlock(fenced.Lines.ToString(), language));
                break;

            case CodeBlock code:
                sink.AddBlock(CodeBlock(code.Lines.ToString(), null));
                break;

            case ThematicBreakBlock:
                sink.AddBlock(PmNode.Element("horizontalRule"));
                break;

            case HtmlBlock htmlBlock:
                sink.AddBlocks(html.Convert(htmlBlock.Lines.ToString(), depth));
                break;

            case Table table:
                Table(table, sink, depth);
                break;

            case LinkReferenceDefinitionGroup or LinkReferenceDefinition:
                // Only what other links point to; the links already carry their addresses.
                break;

            case FootnoteGroup group:
                Notes.Add(NoteCodes.Footnote);
                foreach (var footnote in group.OfType<Footnote>())
                {
                    sink.AddBlocks(Collect(footnote, depth + 1, $"[^{footnote.Label?.TrimStart('^')}]: "));
                }

                break;

            case DefinitionList definitions:
                Notes.Add(NoteCodes.DefinitionList);
                foreach (var item in definitions.OfType<DefinitionItem>())
                {
                    foreach (var part in item)
                    {
                        if (part is DefinitionTerm term)
                        {
                            InlinesOf(term.Inline, sink);
                            sink.EndParagraph();
                        }
                        else
                        {
                            Block(part, sink, depth + 1);
                        }
                    }
                }

                break;

            case ContainerBlock container:
                Blocks(container, sink, depth + 1);
                break;

            case LeafBlock leaf:
                if (leaf.Inline is not null)
                {
                    InlinesOf(leaf.Inline, sink);
                    sink.EndParagraph();
                }
                else if (leaf.Lines.Count > 0)
                {
                    sink.AddBlock(Shapes.Paragraph(PmNode.TextNode(ContentRules.CleanText(leaf.Lines.ToString()))));
                }

                break;
        }
    }

    private static PmNode CodeBlock(string source, string? language)
    {
        var text = ContentRules.CleanText(source).TrimEnd('\n');
        return PmNode.Element(
            "codeBlock", text.Length > 0 ? [PmNode.TextNode(text)] : null, language is null ? null : Shapes.Attributes("language", language));
    }

    private void List(ListBlock list, BlockSink sink, int depth)
    {
        var items = list.OfType<ListItemBlock>()
            .Select(item => (Item: item, Task: (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList))
            .Select(found => new RawItem(found.Task is not null, found.Task?.Checked == true, Collect(found.Item, depth + 1)))
            .ToList();

        sink.AddBlocks(Shapes.Lists(items, list.IsOrdered, list.IsOrdered ? Shapes.ListStart(list.OrderedStart) : 1, depth, Notes));
    }

    private void Table(Table table, BlockSink sink, int depth)
    {
        var rows = table.OfType<TableRow>()
            .Select(row => (IReadOnlyList<RawCell>)
            [
                .. row.OfType<TableCell>().Select(cell => new RawCell(row.IsHeader, Collect(cell, depth + 1), cell.ColumnSpan, cell.RowSpan)),
            ])
            .ToList();

        sink.AddBlocks(Shapes.Table(rows, depth, Notes));
    }

    /// <summary>What the inline HTML tags of one paragraph have turned on, and what they hide.</summary>
    private sealed class InlineHtmlState
    {
        public TextMarks Overlay { get; set; } = new();

        /// <summary>Open tags that changed the marks, innermost last, with the overlay from before each.</summary>
        public List<(string Tag, TextMarks Before)> Open { get; } = [];

        /// <summary>The element whose content is hidden (a script, say), until its closing tag.</summary>
        public string? Hiding { get; set; }

        /// <summary>A file link's text, which the file takes the place of.</summary>
        public StringBuilder? FileLinkText { get; set; }

        public PmNode? FileLink { get; set; }
    }

    private void InlinesOf(ContainerInline? inline, BlockSink sink)
    {
        if (inline is null)
        {
            return;
        }

        var state = new InlineHtmlState();
        Inlines(inline, sink, new TextMarks(), state, 0);
        FinishFileLink(sink, state);
    }

    private static TextMarks Merge(TextMarks a, TextMarks b) => new(
        a.Bold || b.Bold, a.Italic || b.Italic, a.Strike || b.Strike, a.Underline || b.Underline, a.Code || b.Code,
        a.Href ?? b.Href, a.Title ?? b.Title);

    private void Text(string text, BlockSink sink, TextMarks marks, InlineHtmlState state)
    {
        if (state.Hiding is not null)
        {
            return;
        }

        if (state.FileLinkText is not null)
        {
            state.FileLinkText.Append(text);
            return;
        }

        sink.AddText(text, Merge(marks, state.Overlay));
    }

    private void Inlines(ContainerInline container, BlockSink sink, TextMarks marks, InlineHtmlState state, int depth)
    {
        if (depth > HardDepth)
        {
            Notes.Add(NoteCodes.NestingFlattened, "markdown");
            Text(string.Concat(container.Descendants<LiteralInline>().Take(100_000).Select(literal => literal.Content.ToString())), sink, marks, state);
            return;
        }

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Text(literal.Content.ToString(), sink, marks, state);
                    break;

                case HtmlEntityInline entity:
                    Text(entity.Transcoded.ToString(), sink, marks, state);
                    break;

                case CodeInline code:
                    Text(code.Content, sink, marks with { Code = true }, state);
                    break;

                case LineBreakInline:
                    // Joplin shows a line break in the text as a line break, so the editor gets one too.
                    if (state.Hiding is null && state.FileLinkText is null)
                    {
                        sink.AddInline(PmNode.Element("hardBreak"));
                    }

                    break;

                case EmphasisInline emphasis:
                    Inlines(emphasis, sink, Emphasis(emphasis, marks), state, depth + 1);
                    break;

                case AutolinkInline autolink:
                    var target = autolink.IsEmail && !autolink.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        ? "mailto:" + autolink.Url
                        : autolink.Url;
                    Link(target, null, () => autolink.Url, sink, marks, state, null, depth);
                    break;

                case LinkInline link when link.IsImage:
                    if (state.Hiding is null)
                    {
                        context.AddImage(sink, link.Url, PlainText(link), Merge(marks, state.Overlay));
                    }

                    break;

                case LinkInline link:
                    Link(link.Url, link.Title, () => PlainText(link), sink, marks, state, link, depth);
                    break;

                case HtmlInline tag:
                    Tag(tag.Tag, sink, marks, state);
                    break;

                case FootnoteLink { IsBackLink: true }:
                    // The arrow back from the note to its mark; the note has no use for it.
                    break;

                case FootnoteLink footnote:
                    Notes.Add(NoteCodes.Footnote);
                    Text($"[^{footnote.Footnote.Label?.TrimStart('^')}]", sink, marks, state);
                    break;

                case MathInline math:
                    Notes.Add(NoteCodes.Math);
                    var delimiter = new string(math.Delimiter, Math.Max(math.DelimiterCount, 1));
                    Text(delimiter + math.Content + delimiter, sink, marks, state);
                    break;

                case TaskList:
                    break;

                case ContainerInline other:
                    Inlines(other, sink, marks, state, depth + 1);
                    break;
            }
        }
    }

    private TextMarks Emphasis(EmphasisInline emphasis, TextMarks marks)
    {
        switch (emphasis.DelimiterChar)
        {
            case '*' or '_':
                return emphasis.DelimiterCount switch
                {
                    1 => marks with { Italic = true },
                    2 => marks with { Bold = true },
                    _ => marks with { Bold = true, Italic = true },
                };
            case '~' when emphasis.DelimiterCount >= 2:
                return marks with { Strike = true };
            case '+' when emphasis.DelimiterCount >= 2:
                return marks with { Underline = true };
            case '=':
                Notes.Add(NoteCodes.Highlight);
                return marks;
            default:
                // Subscript (~x~) and superscript (^x^): the editor has neither.
                Notes.Add(NoteCodes.SubSuperscript);
                return marks;
        }
    }

    private void Link(
        string? address, string? title, Func<string> visibleText, BlockSink sink, TextMarks marks, InlineHtmlState state,
        LinkInline? link, int depth)
    {
        if (state.Hiding is not null)
        {
            return;
        }

        var file = context.ResolveLink(address, visibleText, out var href);
        if (file is not null)
        {
            sink.AddInline(file);
            return;
        }

        if (link is null)
        {
            // An autolink shows its address, and that is all it has.
            Text(visibleText(), sink, href is not null && marks.Href is null ? marks with { Href = href } : marks, state);
            return;
        }

        var linked = href is not null && marks.Href is null
            ? marks with { Href = href, Title = string.IsNullOrEmpty(title) ? null : ContentRules.Limit(ContentRules.CleanText(title), ContentRules.MaxTitleLength, Notes, "title") }
            : marks;
        Inlines(link, sink, linked, state, depth + 1);
    }

    /// <summary>The text under an inline, for an image's description or a file link's name.</summary>
    private static string PlainText(ContainerInline container) => string.Concat(
        container.Descendants().Take(10_000).Select(inline => inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            _ => "",
        }));

    /// <summary>
    /// A tag in the middle of a paragraph (Markdig hands over each tag on its own): <c>&lt;br&gt;</c>, <c>&lt;u&gt;</c>, an
    /// image, a link. Formatting tags switch the marks on until their closing tag; elements that hold code hide their
    /// content; the rest keeps its text.
    /// </summary>
    private void Tag(string tag, BlockSink sink, TextMarks marks, InlineHtmlState state)
    {
        var match = TagName().Match(tag);
        if (!match.Success)
        {
            // A comment, or a declaration.
            return;
        }

        var name = match.Groups["name"].Value.ToLowerInvariant();
        var closing = tag.AsSpan().TrimStart().StartsWith("</");

        if (state.Hiding is not null)
        {
            if (closing && name == state.Hiding)
            {
                state.Hiding = null;
            }

            return;
        }

        if (closing)
        {
            if (name == "a")
            {
                FinishFileLink(sink, state);
            }

            var at = state.Open.FindLastIndex(open => open.Tag == name);
            if (at >= 0)
            {
                state.Overlay = state.Open[at].Before;
                state.Open.RemoveRange(at, state.Open.Count - at);
            }

            return;
        }

        if (ContentRules.RemovedElements.Contains(name))
        {
            // A void element (an embed) has no content and no closing tag: there is nothing to hide.
            if (!ContentRules.VoidElements.Contains(name) && !tag.TrimEnd().EndsWith("/>"))
            {
                state.Hiding = name;
            }

            if (!ContentRules.SilentlyRemovedElements.Contains(name))
            {
                Notes.Add(NoteCodes.RemovedElement, name);
            }

            return;
        }

        var current = Merge(marks, state.Overlay);
        switch (name)
        {
            case "br":
                if (state.FileLinkText is null)
                {
                    sink.AddInline(PmNode.Element("hardBreak"));
                }

                break;
            case "img":
                var element = html.ParseElement(tag);
                context.AddImage(sink, element?.GetAttribute("src"), element?.GetAttribute("alt") ?? "", current);
                break;
            case "a":
                var anchor = html.ParseElement(tag);
                var file = context.ResolveLink(anchor?.GetAttribute("href"), () => "", out var href);
                if (file is not null)
                {
                    state.FileLink = file;
                    state.FileLinkText = new StringBuilder();
                }
                else
                {
                    Push(state, name, o => href is not null && o.Href is null && current.Href is null ? o with { Href = href } : o);
                }

                break;
            case "b" or "strong":
                Push(state, name, o => o with { Bold = true });
                break;
            case "i" or "em":
                Push(state, name, o => o with { Italic = true });
                break;
            case "u":
                Push(state, name, o => o with { Underline = true });
                break;
            case "s" or "del" or "strike":
                Push(state, name, o => o with { Strike = true });
                break;
            case "code" or "kbd" or "samp" or "tt":
                Push(state, name, o => o with { Code = true });
                break;
            default:
                if (!ContentRules.AllowedTags.Contains(name) && !ContentRules.IsLayoutElement(name))
                {
                    Notes.Add(NoteCodes.UnsupportedElement, name);
                }

                break;
        }
    }

    private static void Push(InlineHtmlState state, string tag, Func<TextMarks, TextMarks> change)
    {
        state.Open.Add((tag, state.Overlay));
        state.Overlay = change(state.Overlay);
    }

    /// <summary>The file a link to a resource stands for takes the place of the link and its text.</summary>
    private void FinishFileLink(BlockSink sink, InlineHtmlState state)
    {
        if (state.FileLink is null)
        {
            return;
        }

        var text = state.FileLinkText?.ToString().Trim() ?? "";
        var fileName = state.FileLink.Attrs?["fileName"] as string;
        if (text.Length > 0 && !string.Equals(text, fileName, StringComparison.Ordinal))
        {
            Notes.Add(NoteCodes.ResourceLinkTextLost);
        }

        sink.AddInline(state.FileLink);
        state.FileLink = null;
        state.FileLinkText = null;
    }
}
