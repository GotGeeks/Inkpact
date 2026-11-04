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
        foreach (var node in nodes)
        {
            if (Block(node) is { Length: > 0 } block)
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    private string? Block(DocNode node)
    {
        switch (node.Type)
        {
            case "paragraph":
                return MarkdownInline.Write(node.Content, InlineContext.Paragraph, links);
            case "heading":
                {
                    var level = Math.Clamp(node.Int("level", 1), 1, 6);
                    var text = MarkdownInline.Write(node.Content, InlineContext.Heading, links);
                    // The closing sequence of a heading ("## a #") is not text.
                    if (text.EndsWith('#'))
                    {
                        text = text[..^1] + "\\#";
                    }

                    return new string('#', level) + (text.Length > 0 ? " " + text : "");
                }
            case "blockquote":
                return Prefixed(Blocks(node.Content), "> ", ">");
            case "bulletList":
                return List(node, _ => "- ");
            case "orderedList":
                {
                    var start = Math.Max(0, node.Int("start", 1));
                    return List(node, index => $"{start + index}. ");
                }
            case "taskList":
                return List(node, _ => "- ", task: true);
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
                text.Add(prefix.TrimEnd());
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
        var code = string.Concat(node.Content.Where(n => n.Type == "text").Select(n => n.Text));
        var longest = 0;
        var run = 0;
        foreach (var c in code)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        // The language is a word: anything else would end the fence's line or mean something else.
        var language = node.String("language") is { Length: > 0 } l && l.All(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '#' or '.' or '-') ? l : "";
        return code.Length == 0 ? $"{fence}{language}\n{fence}" : $"{fence}{language}\n{code}\n{fence}";
    }

    // ---- tables --------------------------------------------------------------------------------------------------------

    private string Table(DocNode table)
    {
        var rows = table.Content.Where(r => r.Type == "tableRow").ToList();
        return rows.Count == 0 ? "" : FitsPipeTable(rows) ? PipeTable(rows) : HtmlTable(table);
    }

    /// <summary>A pipe table has a header row and nothing but cells of one inline paragraph, one to a column, none merged.</summary>
    private static bool FitsPipeTable(List<DocNode> rows)
    {
        var width = rows[0].Content.Count;
        if (width == 0 || rows[0].Content.Any(c => c.Type != "tableHeader") || rows.Skip(1).Any(r => r.Content.Any(c => c.Type != "tableCell")))
        {
            return false;
        }

        return rows.All(r => r.Content.Count == width && r.Content.All(c =>
            c.Int("colspan", 1) == 1 && c.Int("rowspan", 1) == 1 && c.Content.Count <= 1 && c.Content.All(b => b.Type == "paragraph")));
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
                    var code = string.Concat(node.Content.Where(n => n.Type == "text").Select(n => n.Text));
                    var language = node.String("language") is { Length: > 0 } l && l.All(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '#' or '.' or '-') ? $" class=\"language-{l}\"" : "";
                    // A blank line would end the HTML block; a line with one space in it does not.
                    return $"<pre><code{language}>" + WebUtility.HtmlEncode(code).Replace("\n\n", "\n \n", StringComparison.Ordinal) + "</code></pre>";
                }
            case "horizontalRule":
                return "<hr>";
            case "image":
                return Guid.TryParse(node.String("attachmentId"), out var id)
                    ? $"<img src=\"{HtmlAttribute(links.PathOfAttachment(id) ?? $"attachment:{id:D}")}\" alt=\"{HtmlAttribute(node.String("alt") ?? "")}\">"
                    : "";
            case "table":
                return HtmlTable(node).Replace("\n", "", StringComparison.Ordinal);
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
                        // Outermost first, as the Markdown writer orders marks, so that equal marks are written equally.
                        foreach (var mark in node.Marks.OrderByDescending(m => Array.IndexOf(["link", "underline", "bold", "italic", "strike", "code"], m.Type)))
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
