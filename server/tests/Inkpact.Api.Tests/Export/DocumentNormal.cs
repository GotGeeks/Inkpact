using System.Text.Json.Nodes;

namespace Inkpact.Api.Tests.Export;

/// <summary>
/// A document in the one shape that two documents with the same meaning share, for comparing a page with what its Markdown
/// is read back as. Only what Markdown cannot carry or what is the same said two ways is made equal: defaults and nulls,
/// the order of marks, text split in pieces, empty paragraphs (Markdown has no blank line to keep one with), the space at the
/// ends of a paragraph, and what the import does not read back
/// of a table (a column's alignment and width) or of a link (its target and rel).
/// </summary>
internal static class DocumentNormal
{
    /// <summary>The writer's order: where white space between two runs is in their marks follows it (see <see cref="Split"/>).</summary>
    private static readonly string[] MarkOrder = Inkpact.Api.Export.MarkdownInline.MarkOrder;

    /// <summary>Where two documents first differ, for a failure message: the path, and what each has there.</summary>
    public static string FirstDifference(JsonNode? a, JsonNode? b, string path = "$")
    {
        if (JsonNode.DeepEquals(a, b))
        {
            return "";
        }

        if (a is JsonArray left && b is JsonArray right)
        {
            for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                if (FirstDifference(i < left.Count ? left[i] : null, i < right.Count ? right[i] : null, $"{path}[{i}]") is { Length: > 0 } found)
                {
                    return found;
                }
            }
        }

        if (a is JsonObject x && b is JsonObject y)
        {
            foreach (var name in x.Select(p => p.Key).Union(y.Select(p => p.Key)))
            {
                if (FirstDifference(x[name], y[name], $"{path}.{name}") is { Length: > 0 } found)
                {
                    return found;
                }
            }
        }

        return $"{path}\n  before: {a?.ToJsonString()}\n  back:   {b?.ToJsonString()}";
    }

    public static JsonNode Of(JsonNode document)
    {
        var root = (JsonObject)document.DeepClone();
        Blocks(root);
        return root;
    }

    private static void Blocks(JsonObject parent)
    {
        if (parent["content"] is not JsonArray content)
        {
            return;
        }

        var children = content.Select(c => (JsonObject)c!).ToList();
        content.Clear();
        foreach (var child in children)
        {
            Node(child);
        }

        var kept = new List<JsonObject>();
        foreach (var child in children)
        {
            var type = child["type"]!.GetValue<string>();
            if (type is "paragraph" or "heading" && child["content"] is not JsonArray { Count: > 0 })
            {
                continue;
            }

            kept.Add(child);
        }

        parent["content"] = new JsonArray(kept.Select(c => (JsonNode)c).ToArray());
        if (kept.Count == 0)
        {
            parent.Remove("content");
        }
    }

    private static void Node(JsonObject node)
    {
        var type = node["type"]!.GetValue<string>();
        Attributes(node, type);
        if (type == "text")
        {
            Marks(node);
            return;
        }

        Blocks(node);
        if (node["content"] is JsonArray content && type is "paragraph" or "heading" or "codeBlock")
        {
            Inline(node, content, type);
        }
    }

    private static void Attributes(JsonObject node, string type)
    {
        if (type == "taskItem" && node["attrs"] is null)
        {
            node["attrs"] = new JsonObject();
        }

        if (node["attrs"] is not JsonObject attrs)
        {
            return;
        }

        foreach (var name in attrs.Select(a => a.Key).ToList())
        {
            var value = attrs[name];
            var drop = value is null
                || (name is "colspan" or "rowspan" && value.GetValue<int>() == 1)
                || name is "align" or "colwidth"
                || (type == "orderedList" && (name == "type" || (name == "start" && value.GetValue<int>() == 1)))
                || (type == "codeBlock" && name == "language" && value.GetValue<string>().Length == 0)
                || (type == "image" && name == "alt" && value.GetValue<string>().Length == 0);
            if (drop)
            {
                attrs.Remove(name);
            }
        }

        if (type == "taskItem")
        {
            attrs["checked"] ??= false;
        }

        if (attrs.Count == 0)
        {
            node.Remove("attrs");
        }
    }

    private static void Marks(JsonObject text)
    {
        if (text["marks"] is not JsonArray marks)
        {
            return;
        }

        var kept = new List<JsonObject>();
        var marked = marks.Select(m => (JsonObject)m!).ToList();
        marks.Clear();
        // The import reads a code span as code and nothing else, whatever it sits inside (`**`x`**` is code): the export is right
        // for any other reader, which shows it bold, and what is compared is what the import gives back.
        if (marked.Any(m => m["type"]!.GetValue<string>() == "code"))
        {
            marked = [.. marked.Where(m => m["type"]!.GetValue<string>() == "code")];
        }

        foreach (var mark in marked)
        {
            if (mark["type"]!.GetValue<string>() == "link")
            {
                var attrs = new JsonObject();
                foreach (var name in new[] { "href", "title" })
                {
                    if (mark["attrs"]?[name] is { } value && value.GetValue<string>().Length > 0)
                    {
                        attrs[name] = value.DeepClone();
                    }
                }

                mark.Remove("attrs");
                if (attrs.Count > 0)
                {
                    mark["attrs"] = attrs;
                }
            }

            kept.Add(mark);
        }

        text["marks"] = new JsonArray(kept.OrderBy(m => Array.IndexOf(MarkOrder, m["type"]!.GetValue<string>())).Select(m => (JsonNode)m).ToArray());
    }

    /// <summary>
    /// Where white space between two pieces of text is in the marks. The writer closes the marks that end, then writes the
    /// space, then opens the marks that begin, so the space is inside the marks the two pieces share from the outside in (the
    /// longest common start of their marks, in the fixed order), and outside all the others. At the end of a block, or next to a
    /// break or a file, it is outside every mark. Code keeps the white space it has.
    /// </summary>
    private static List<JsonObject> Split(List<JsonObject> nodes)
    {
        var result = new List<JsonObject>();
        var pending = "";
        JsonObject? last = null;

        List<JsonObject> MarksOf(JsonObject node) => (node["marks"] as JsonArray)?.Select(m => (JsonObject)m!).ToList() ?? [];

        void Flush(JsonObject? next)
        {
            if (pending.Length == 0)
            {
                return;
            }

            var space = new JsonObject { ["type"] = "text", ["text"] = pending };
            if (last is not null && next is not null)
            {
                var before = MarksOf(last);
                var after = MarksOf(next);
                var shared = new JsonArray();
                for (var m = 0; m < before.Count && m < after.Count && KeyOf(before[m]) == KeyOf(after[m]) && !KeyOf(before[m]).StartsWith("code|", StringComparison.Ordinal); m++)
                {
                    shared.Add(before[m].DeepClone());
                }

                if (shared.Count > 0)
                {
                    space["marks"] = shared;
                }
            }

            result.Add(space);
            pending = "";
        }

        foreach (var node in nodes)
        {
            var isText = node["type"]!.GetValue<string>() == "text";
            if (!isText)
            {
                Flush(null);
                result.Add(node);
                last = null;
                continue;
            }

            if (MarkKeys(node).Any(k => k.StartsWith("code|", StringComparison.Ordinal)))
            {
                Flush(node);
                result.Add(node);
                last = node;
                continue;
            }

            var text = node["text"]!.GetValue<string>();
            var core = text.Trim();
            if (core.Length == 0)
            {
                pending += text;
                continue;
            }

            var lead = text.Length - text.TrimStart().Length;
            var trail = text.Length - text.TrimEnd().Length;
            pending += text[..lead];
            node["text"] = core;
            Flush(node);
            result.Add(node);
            last = node;
            pending = text[^trail..].Length == 0 || trail == 0 ? "" : text[^trail..];
        }

        Flush(null);
        return result;
    }

    private static IEnumerable<string> MarkKeys(JsonObject node) =>
        node["marks"] is JsonArray marks ? marks.Select(m => KeyOf((JsonObject)m!)) : [];

    private static string KeyOf(JsonObject mark) =>
        mark["type"]!.GetValue<string>() + "|" + mark["attrs"]?["href"] + "|" + mark["attrs"]?["title"];

    /// <summary>Text in pieces with the same marks is one piece; the space at the ends of a block is not kept; a break at either end says nothing.</summary>
    private static void Inline(JsonObject block, JsonArray content, string type)
    {
        var all = Split(content.Select(n => (JsonObject)n!).ToList());
        content.Clear();
        var pieces = new List<JsonObject>();
        foreach (var node in all)
        {
            if (node["type"]!.GetValue<string>() == "text" && node["text"]!.GetValue<string>().Length == 0)
            {
                continue;
            }

            if (pieces.Count > 0 && node["type"]!.GetValue<string>() == "text" && pieces[^1]["type"]!.GetValue<string>() == "text"
                && Equals(pieces[^1]["marks"]?.ToJsonString(), node["marks"]?.ToJsonString()))
            {
                pieces[^1]["text"] = pieces[^1]["text"]!.GetValue<string>() + node["text"]!.GetValue<string>();
                continue;
            }

            pieces.Add(node);
        }

        if (type != "codeBlock")
        {
            // Breaks and white space at either end say nothing, and neither does a break with only white space after it.
            bool Blank(JsonObject p) => p["type"]!.GetValue<string>() == "hardBreak" || (p["type"]!.GetValue<string>() == "text" && string.IsNullOrWhiteSpace(p["text"]!.GetValue<string>()));
            while (pieces.Count > 0 && Blank(pieces[^1]))
            {
                pieces.RemoveAt(pieces.Count - 1);
            }

            while (pieces.Count > 0 && Blank(pieces[0]))
            {
                pieces.RemoveAt(0);
            }

            // A line starts where it starts: Markdown does not keep white space after a break.
            for (var i = 1; i < pieces.Count; i++)
            {
                if (pieces[i - 1]["type"]!.GetValue<string>() == "hardBreak" && pieces[i]["type"]!.GetValue<string>() == "text")
                {
                    pieces[i]["text"] = pieces[i]["text"]!.GetValue<string>().TrimStart();
                }
            }

            pieces.RemoveAll(p => p["type"]!.GetValue<string>() == "text" && p["text"]!.GetValue<string>().Length == 0);
            if (pieces.Count > 0 && pieces[0]["type"]!.GetValue<string>() == "text")
            {
                pieces[0]["text"] = pieces[0]["text"]!.GetValue<string>().TrimStart();
            }

            if (pieces.Count > 0 && pieces[^1]["type"]!.GetValue<string>() == "text")
            {
                pieces[^1]["text"] = pieces[^1]["text"]!.GetValue<string>().TrimEnd();
            }

            pieces.RemoveAll(p => p["type"]!.GetValue<string>() == "text" && p["text"]!.GetValue<string>().Length == 0);
        }
        else if (pieces.Count > 0 && pieces[^1]["type"]!.GetValue<string>() == "text")
        {
            pieces[^1]["text"] = pieces[^1]["text"]!.GetValue<string>().TrimEnd();
        }

        block["content"] = new JsonArray(pieces.Select(p => (JsonNode)p).ToArray());
        if (pieces.Count == 0)
        {
            block.Remove("content");
        }
    }
}
