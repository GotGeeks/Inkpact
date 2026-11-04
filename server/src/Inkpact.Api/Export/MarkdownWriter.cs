using System.Net;
using System.Text;

namespace Inkpact.Api.Export;

/// <summary>
/// A page's document (the editor's ProseMirror JSON, as the sidecar's <c>read</c> gives it) as Markdown that reads without
/// Inkpact: CommonMark with the GitHub tables and task lists. What Markdown has no form for is written as HTML, which
/// Markdown allows: underline, and a table with a merged cell or more than a paragraph in a cell. An empty paragraph says
/// nothing in Markdown and is left out. The front matter with the page's title and dates is the zip's (run 1.18b).
/// </summary>
public sealed class MarkdownWriter(ExportLinks links)
{
    /// <summary>The document as Markdown, ending with a line break unless it is empty.</summary>
    public string Write(ReadOnlySpan<byte> documentJson)
    {
        var root = DocNode.Parse(documentJson);
        var blocks = Blocks(root.Content);
        return blocks.Count == 0 ? "" : string.Join("\n\n", blocks) + "\n";
    }

    private List<string> Blocks(IEnumerable<DocNode> nodes)
    {
        var blocks = new List<string>();
        // Two lists that touch are one list in Markdown, unless the second has the other marker: "-" and "*", "." and ")".
        var lastMarker = '\0';
        foreach (var node in nodes)
        {
            var marker = node.Type switch
            {
                "bulletList" or "taskList" => lastMarker == '-' ? '*' : '-',
                "orderedList" => lastMarker == '.' ? ')' : '.',
                _ => '\0',
            };
            if (Block(node, marker) is { Length: > 0 } block)
            {
                blocks.Add(block);
                lastMarker = marker;
            }
        }

        return blocks;
    }

    private string? Block(DocNode node, char marker)
    {
        switch (node.Type)
        {
            case "paragraph":
                return MarkdownInline.Write(node.Content, InlineContext.Paragraph, links);
            case "heading":
                {
                    var level = Math.Clamp(node.Int("level", 1), 1, 6);
                    var text = MarkdownInline.Write(node.Content, InlineContext.Heading, links);
                    // Hashes at the end after a space ("## a #") are the heading's closing sequence, not text.
                    var hashes = text.Length - text.TrimEnd('#').Length;
                    if (hashes > 0 && (hashes == text.Length || text[^(hashes + 1)] is ' ' or '\t'))
                    {
                        text = text[..^hashes] + "\\" + text[^hashes..];
                    }

                    return new string('#', level) + (text.Length > 0 ? " " + text : "");
                }
            case "blockquote":
                return Prefixed(Blocks(node.Content), "> ", ">");
            case "bulletList":
                return List(node, _ => $"{marker} ");
            case "orderedList":
                {
                    var start = Math.Max(0, node.Int("start", 1));
                    return List(node, index => $"{start + index}{marker} ");
                }
            case "taskList":
                return List(node, _ => $"{marker} ", task: true);
            case "codeBlock":
                return CodeBlock(node);
            case "horizontalRule":
                return "---";
            case "image":
                return Guid.TryParse(node.String("attachmentId"), out var id)
                    ? MarkdownInline.Image(node, links.PathOfAttachment(id) ?? $"attachment:{id:D}")
                    : null;
            case "table":
                return Table(node);
            default:
                return null;
        }
    }

    private string List(DocNode list, Func<int, string> marker, bool task = false)
    {
        var items = list.Content.Where(item => item.Type is "listItem" or "taskItem").ToList();
        var rendered = items.Select(item => Blocks(item.Content)).ToList();
        // Items are close together unless one of them holds more than a block, which makes the list loose.
        var separator = rendered.Any(blocks => blocks.Count > 1) ? "\n\n" : "\n";
        var text = new List<string>();
        for (var i = 0; i < items.Count; i++)
        {
            var prefix = marker(i) + (task ? (items[i].Bool("checked") ? "[x] " : "[ ] ") : "");
            var indent = new string(' ', marker(i).Length);
            var body = string.Join("\n\n", rendered[i]);
            if (body.Length == 0)
            {
                // A checkbox with nothing after it is text to GitHub: a space it keeps makes it a checkbox again.
                text.Add(task ? prefix + "&nbsp;" : prefix.TrimEnd());
                continue;
            }

            var lines = body.Split('\n');
            var builder = new StringBuilder(prefix).Append(lines[0]);
            for (var l = 1; l < lines.Length; l++)
            {
                builder.Append('\n').Append(lines[l].Length == 0 ? "" : indent + lines[l]);
            }

            text.Add(builder.ToString());
        }

        return string.Join(separator, text);
    }

    private static string Prefixed(List<string> blocks, string prefix, string emptyPrefix)
    {
        var lines = string.Join("\n\n", blocks).Split('\n');
        return string.Join('\n', lines.Select(line => line.Length == 0 ? emptyPrefix : prefix + line));
    }

    private static string CodeBlock(DocNode node)
    {
        var code = CodeOf(node);
        var fence = new string('`', Math.Max(3, MarkdownInline.LongestBacktickRun(code) + 1));
        var language = LanguageOf(node);
        return code.Length == 0 ? $"{fence}{language}\n{fence}" : $"{fence}{language}\n{code}\n{fence}";
    }

    private static string CodeOf(DocNode codeBlock) => string.Concat(codeBlock.Content.Where(n => n.Type == "text").Select(n => n.Text));

    /// <summary>A code block's language if it is a word, otherwise none: anything else would end the fence's line or mean something else.</summary>
    private static string LanguageOf(DocNode codeBlock) =>
        codeBlock.String("language") is { Length: > 0 } l && l.All(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '#' or '.' or '-') ? l : "";

    // ---- tables --------------------------------------------------------------------------------------------------------

    private string Table(DocNode table)
    {
        var rows = table.Content.Where(r => r.Type == "tableRow").ToList();
        return rows.Count == 0 ? "" : FitsPipeTable(rows) ? PipeTable(rows) : HtmlTable(table);
    }

    /// <summary>
    /// A pipe table has a header row and nothing but cells of one inline paragraph, one to a column, none merged, and no "|"
    /// in code: GitHub ends a cell at every "|" without a backslash, also in code, where the import would keep the backslash.
    /// (Elsewhere a "|" has one: in text, and in a link's address and title, where a backslash is read the same way.)
    /// </summary>
    private static bool FitsPipeTable(List<DocNode> rows)
    {
        var width = rows[0].Content.Count;
        if (width == 0 || rows[0].Content.Any(c => c.Type != "tableHeader") || rows.Skip(1).Any(r => r.Content.Any(c => c.Type != "tableCell")))
        {
            return false;
        }

        return rows.All(r => r.Content.Count == width && r.Content.All(c =>
            c.Int("colspan", 1) == 1 && c.Int("rowspan", 1) == 1 && c.Content.Count <= 1 && c.Content.All(b => b.Type == "paragraph"
                && !b.Content.Any(n => n.Type == "text" && n.Marks.Any(m => m.Type == "code") && (n.Text ?? "").Contains('|')))));
    }

    private string PipeTable(List<DocNode> rows)
    {
        string Cells(DocNode row) =>
            "| " + string.Join(" | ", row.Content.Select(cell => cell.Content.Count == 0 ? "" : MarkdownInline.Write(cell.Content[0].Content, InlineContext.Cell, links))) + " |";

        var lines = new List<string> { Cells(rows[0]) };
        lines.Add("| " + string.Join(" | ", rows[0].Content.Select(cell => cell.String("align") switch
        {
            "left" => ":---",
            "center" => ":---:",
            "right" => "---:",
            _ => "---",
        })) + " |");
        lines.AddRange(rows.Skip(1).Select(Cells));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// A table as HTML, for what a pipe table cannot hold. No blank line in it: an HTML block ends at one, so the cells'
    /// content is HTML too, not Markdown.
    /// </summary>
    private string HtmlTable(DocNode table)
    {
        var html = new StringBuilder("<table>\n");
        foreach (var row in table.Content.Where(r => r.Type == "tableRow"))
        {
            html.Append("<tr>\n");
            foreach (var cell in row.Content.Where(c => c.Type is "tableCell" or "tableHeader"))
            {
                var tag = cell.Type == "tableHeader" ? "th" : "td";
                html.Append('<').Append(tag);
                if (cell.Int("colspan", 1) > 1)
                {
                    html.Append(" colspan=\"").Append(cell.Int("colspan", 1)).Append('"');
                }

                if (cell.Int("rowspan", 1) > 1)
                {
                    html.Append(" rowspan=\"").Append(cell.Int("rowspan", 1)).Append('"');
                }

                html.Append('>').Append(HtmlBlocks(cell.Content)).Append("</").Append(tag).Append(">\n");
            }

            html.Append("</tr>\n");
        }

        return html.Append("</table>").ToString();
    }

    private string HtmlBlocks(IEnumerable<DocNode> nodes) => string.Concat(nodes.Select(HtmlBlock));

    private string HtmlBlock(DocNode node)
    {
        switch (node.Type)
        {
            case "paragraph":
                return "<p>" + HtmlInline(node.Content) + "</p>";
            case "heading":
                {
                    var level = Math.Clamp(node.Int("level", 1), 1, 6);
                    return $"<h{level}>{HtmlInline(node.Content)}</h{level}>";
                }
            case "blockquote":
                return "<blockquote>" + HtmlBlocks(node.Content) + "</blockquote>";
            case "bulletList" or "taskList":
                return "<ul>" + string.Concat(node.Content.Select(HtmlItem)) + "</ul>";
            case "orderedList":
                return $"<ol start=\"{Math.Max(0, node.Int("start", 1))}\">" + string.Concat(node.Content.Select(HtmlItem)) + "</ol>";
            case "codeBlock":
                {
                    var language = LanguageOf(node) is { Length: > 0 } l ? $" class=\"language-{l}\"" : "";
                    // A blank line (also one of spaces) would end the HTML block: the line break before it is written as a character reference.
                    var lines = WebUtility.HtmlEncode(CodeOf(node)).Split('\n');
                    var code = new StringBuilder(lines[0]);
                    foreach (var line in lines.Skip(1))
                    {
                        code.Append(string.IsNullOrWhiteSpace(line) ? "&#10;" : "\n").Append(line);
                    }

                    return $"<pre><code{language}>{code}</code></pre>";
                }
            case "horizontalRule":
                return "<hr>";
            case "image":
                return Guid.TryParse(node.String("attachmentId"), out var id)
                    ? $"<img src=\"{HtmlAttribute(links.PathOfAttachment(id) ?? $"attachment:{id:D}")}\" alt=\"{HtmlAttribute(node.String("alt") ?? "")}\">"
                    : "";
            case "table":
                return HtmlTable(node);
            default:
                return "";
        }
    }

    private string HtmlItem(DocNode item)
    {
        var box = item.Type == "taskItem" ? "<input type=\"checkbox\"" + (item.Bool("checked") ? " checked" : "") + "> " : "";
        return "<li>" + box + HtmlBlocks(item.Content) + "</li>";
    }

    private string HtmlInline(IEnumerable<DocNode> nodes)
    {
        var html = new StringBuilder();
        foreach (var node in nodes)
        {
            switch (node.Type)
            {
                case "text" when !string.IsNullOrEmpty(node.Text):
                    {
                        var piece = WebUtility.HtmlEncode(node.Text.Replace('\n', ' '));
                        // From the inside out, in the order the Markdown is written in.
                        foreach (var mark in node.Marks.OrderByDescending(m => Array.IndexOf(MarkdownInline.MarkOrder, m.Type)))
                        {
                            piece = mark.Type switch
                            {
                                "bold" => $"<strong>{piece}</strong>",
                                "italic" => $"<em>{piece}</em>",
                                "strike" => $"<del>{piece}</del>",
                                "underline" => $"<u>{piece}</u>",
                                "code" => $"<code>{piece}</code>",
                                "link" when !string.IsNullOrEmpty(mark.Text("href")) =>
                                    $"<a href=\"{HtmlAttribute(PageOrOwn(mark.Text("href")!))}\"{(mark.Text("title") is { Length: > 0 } title ? $" title=\"{HtmlAttribute(title)}\"" : "")}>{piece}</a>",
                                _ => piece,
                            };
                        }

                        html.Append(piece);
                        break;
                    }
                case "hardBreak":
                    html.Append("<br>");
                    break;
                case "attachment" when Guid.TryParse(node.String("attachmentId"), out var id):
                    html.Append("<a href=\"").Append(HtmlAttribute(links.PathOfAttachment(id) ?? $"attachment:{id:D}")).Append("\">")
                        .Append(WebUtility.HtmlEncode(node.String("fileName") is { Length: > 0 } name ? name : "file")).Append("</a>");
                    break;
            }
        }

        return html.ToString();
    }

    private static string HtmlAttribute(string value) => WebUtility.HtmlEncode(value);

    /// <summary>The address a link is written with: the path of another page of the export for <c>/p/&lt;id&gt;</c>, otherwise as it is.</summary>
    internal string PageOrOwn(string href) => MarkdownInline.PageOrOwn(href, links);
}

/// <summary>Links between pages: <c>/p/&lt;id&gt;</c> as the editor and the import write them.</summary>
internal static class ExportPageLinks
{
    public static Guid? PageIdOf(string href) =>
        href.StartsWith("/p/", StringComparison.Ordinal) && Guid.TryParseExact(href[3..], "D", out var id) ? id : null;
}
