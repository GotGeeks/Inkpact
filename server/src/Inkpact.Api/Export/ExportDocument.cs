using System.Text.Json;

namespace Inkpact.Api.Export;

/// <summary>A mark on a text node, as the sidecar's <c>read</c> writes it.</summary>
internal sealed record DocMark(string Type, IReadOnlyDictionary<string, JsonElement> Attrs)
{
    public string? Text(string name) => Attrs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A node of a page's document (ProseMirror JSON), read from the sidecar's answer: its type, attributes, children, and for text its marks.</summary>
internal sealed record DocNode(
    string Type,
    IReadOnlyDictionary<string, JsonElement> Attrs,
    IReadOnlyList<DocNode> Content,
    string? Text,
    IReadOnlyList<DocMark> Marks)
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoAttrs = new Dictionary<string, JsonElement>();

    public int Int(string name, int fallback) =>
        Attrs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    public string? String(string name) => Attrs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public bool Bool(string name) => Attrs.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>The document of <paramref name="json"/>. Deeper than <paramref name="maxDepth"/> is left out, so a hostile page cannot exhaust the stack.</summary>
    public static DocNode Parse(ReadOnlySpan<byte> json, int maxDepth = 200)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 1024 });
        return Read(document.RootElement, 0, maxDepth);
    }

    private static DocNode Read(JsonElement element, int depth, int maxDepth)
    {
        var type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        var attrs = element.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object ? Attributes(a) : NoAttrs;
        var text = element.TryGetProperty("text", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

        var content = new List<DocNode>();
        if (depth < maxDepth && element.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in c.EnumerateArray())
            {
                content.Add(Read(child, depth + 1, maxDepth));
            }
        }

        var marks = new List<DocMark>();
        if (element.TryGetProperty("marks", out var m) && m.ValueKind == JsonValueKind.Array)
        {
            foreach (var mark in m.EnumerateArray())
            {
                var name = mark.TryGetProperty("type", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString()! : "";
                marks.Add(new DocMark(name, mark.TryGetProperty("attrs", out var ma) && ma.ValueKind == JsonValueKind.Object ? Attributes(ma) : NoAttrs));
            }
        }

        return new DocNode(type, attrs, content, text, marks);
    }

    private static Dictionary<string, JsonElement> Attributes(JsonElement element)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }
}
