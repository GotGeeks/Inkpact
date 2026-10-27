using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inkpact.Api.Import;

/// <summary>A mark on a piece of text, in the names TipTap gives it: <c>bold</c>, <c>italic</c>, <c>link</c> and so on.</summary>
public sealed record PmMark(string Type, IReadOnlyDictionary<string, object?>? Attrs = null);

/// <summary>
/// A node of the editor's document (ProseMirror JSON), in the names and attributes of the editor's schema. The import
/// converts to this, the Yjs sidecar writes it as the page's first update, and a test in <c>web/</c> checks that the
/// editor reads back exactly this.
/// </summary>
public sealed class PmNode
{
    private PmNode(string type) => Type = type;

    public string Type { get; }

    public Dictionary<string, object?>? Attrs { get; private init; }

    public List<PmNode>? Content { get; private init; }

    public string? Text { get; private init; }

    public IReadOnlyList<PmMark>? Marks { get; private init; }

    public static PmNode Doc(IEnumerable<PmNode> blocks) => new("doc") { Content = [.. blocks] };

    public static PmNode Element(string type, IEnumerable<PmNode>? content = null, Dictionary<string, object?>? attrs = null) =>
        new(type) { Content = content is null ? null : [.. content], Attrs = attrs is { Count: > 0 } ? attrs : null };

    public static PmNode TextNode(string text, IReadOnlyList<PmMark>? marks = null) =>
        new("text") { Text = text, Marks = marks is { Count: > 0 } ? marks : null };

    public PmNode WithContent(IEnumerable<PmNode> content) => new(Type) { Attrs = Attrs, Content = [.. content] };

    public JsonObject ToJson()
    {
        var node = new JsonObject { ["type"] = Type };
        if (Attrs is { Count: > 0 })
        {
            node["attrs"] = AttributesToJson(Attrs);
        }

        if (Content is { Count: > 0 })
        {
            var content = new JsonArray();
            foreach (var child in Content)
            {
                content.Add(child.ToJson());
            }

            node["content"] = content;
        }

        if (Text is not null)
        {
            node["text"] = Text;
        }

        if (Marks is { Count: > 0 })
        {
            var marks = new JsonArray();
            foreach (var mark in Marks)
            {
                var json = new JsonObject { ["type"] = mark.Type };
                if (mark.Attrs is { Count: > 0 })
                {
                    json["attrs"] = AttributesToJson(mark.Attrs);
                }

                marks.Add(json);
            }

            node["marks"] = marks;
        }

        return node;
    }

    /// <summary>The JSON the sidecar's build operation takes.</summary>
    public byte[] ToUtf8Json()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            ToJson().WriteTo(writer);
        }

        return stream.ToArray();
    }

    private static JsonObject AttributesToJson(IReadOnlyDictionary<string, object?> attributes)
    {
        var json = new JsonObject();
        foreach (var (name, value) in attributes)
        {
            json[name] = value switch
            {
                null => null,
                string text => text,
                bool flag => flag,
                int number => number,
                long number => number,
                int[] numbers => new JsonArray(numbers.Select(n => (JsonNode?)n).ToArray()),
                _ => throw new InvalidOperationException($"Attribute {name} has a value of {value.GetType()}, which a document cannot hold"),
            };
        }

        return json;
    }
}
