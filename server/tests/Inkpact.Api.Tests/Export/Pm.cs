using System.Text.Json.Nodes;
using Inkpact.Api.Export;

namespace Inkpact.Api.Tests.Export;

/// <summary>A document built the short way, as ProseMirror JSON, for the tests of the Markdown writer.</summary>
internal static class Pm
{
    public static JsonObject Doc(params JsonNode[] blocks) => new() { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(b => b.DeepClone()).ToArray()) };

    public static JsonObject Node(string type, JsonObject? attrs = null, params JsonNode[] content)
    {
        var node = new JsonObject { ["type"] = type };
        if (attrs is not null)
        {
            node["attrs"] = attrs;
        }

        if (content.Length > 0)
        {
            node["content"] = new JsonArray(content.Select(c => c.DeepClone()).ToArray());
        }

        return node;
    }

    public static JsonObject P(params JsonNode[] content) => Node("paragraph", null, content);

    public static JsonObject H(int level, params JsonNode[] content) => Node("heading", new JsonObject { ["level"] = level }, content);

    public static JsonObject T(string text, params string[] marks)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text };
        if (marks.Length > 0)
        {
            node["marks"] = new JsonArray(marks.Select(m => (JsonNode)new JsonObject { ["type"] = m }).ToArray());
        }

        return node;
    }

    public static JsonObject Link(string text, string href, string? title = null, params string[] otherMarks)
    {
        var attrs = new JsonObject { ["href"] = href, ["target"] = "_blank", ["rel"] = "noopener noreferrer nofollow" };
        if (title is not null)
        {
            attrs["title"] = title;
        }

        var marks = new JsonArray(new JsonObject { ["type"] = "link", ["attrs"] = attrs });
        foreach (var mark in otherMarks)
        {
            marks.Add(new JsonObject { ["type"] = mark });
        }

        return new JsonObject { ["type"] = "text", ["text"] = text, ["marks"] = marks };
    }

    public static JsonObject Br() => Node("hardBreak");

    public static JsonObject Li(params JsonNode[] blocks) => Node("listItem", null, blocks);

    public static JsonObject Ul(params JsonNode[] items) => Node("bulletList", null, items);

    public static JsonObject Ol(int start, params JsonNode[] items) => Node("orderedList", new JsonObject { ["start"] = start }, items);

    public static JsonObject Task(bool done, params JsonNode[] blocks) => Node("taskItem", new JsonObject { ["checked"] = done }, blocks);

    public static JsonObject Code(string? language, string text) =>
        Node("codeBlock", language is null ? null : new JsonObject { ["language"] = language }, text.Length == 0 ? [] : [T(text)]);

    public static JsonObject Cell(bool header, JsonNode? content = null, int colspan = 1, int rowspan = 1, string? align = null)
    {
        var attrs = new JsonObject();
        if (colspan != 1)
        {
            attrs["colspan"] = colspan;
        }

        if (rowspan != 1)
        {
            attrs["rowspan"] = rowspan;
        }

        if (align is not null)
        {
            attrs["align"] = align;
        }

        return Node(header ? "tableHeader" : "tableCell", attrs.Count > 0 ? attrs : null, content is null ? [] : [content]);
    }

    public static JsonObject Row(params JsonNode[] cells) => Node("tableRow", null, cells);

    public static JsonObject Table(params JsonNode[] rows) => Node("table", null, rows);

    public static JsonObject Image(Guid id, string alt = "") => Node("image", new JsonObject { ["attachmentId"] = id.ToString(), ["alt"] = alt });

    public static JsonObject File(Guid id, string name, long size = 10) =>
        Node("attachment", new JsonObject { ["attachmentId"] = id.ToString(), ["fileName"] = name, ["size"] = size });

    public static string Markdown(JsonNode document, ExportLinks? links = null) =>
        new MarkdownWriter(links ?? ExportLinks.None).Write(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
}
