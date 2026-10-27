using System.Text.Json;
using System.Text.Json.Nodes;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

/// <summary>A resolver over a fixed list of resources.</summary>
internal sealed class FakeResources(IEnumerable<(string Id, ImportedResource Resource)> resources) : IResourceResolver
{
    private readonly Dictionary<string, ImportedResource> _byId = resources.ToDictionary(pair => pair.Id, pair => pair.Resource);

    public ImportedResource? Find(string joplinResourceId) => _byId.GetValueOrDefault(joplinResourceId);
}

/// <summary>
/// The conversion cases in <c>Fixtures/import/conversions.json</c>: Markdown or HTML, and the document it must become.
/// The same file is read by the web tests, which load each expected document through the sidecar into the real editor.
/// </summary>
internal sealed record ConversionCase(string Name, string? Markdown, string? Html, string[] Notes, JsonNode Expected)
{
    private static readonly Lazy<JsonObject> File = new(() =>
        JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "import", "conversions.json")))!.AsObject());

    public static FakeResources Resources { get; } = new(File.Value["resources"]!.AsArray().Select(resource => (
        resource!["id"]!.GetValue<string>(),
        new ImportedResource(
            Guid.Parse(resource["attachmentId"]!.GetValue<string>()),
            resource["fileName"]!.GetValue<string>(),
            resource["size"]!.GetValue<long>(),
            resource["isInlineImage"]!.GetValue<bool>()))));

    public static TheoryData<string> MarkdownNames => Names("markdown");

    public static TheoryData<string> HtmlNames => Names("html");

    private static TheoryData<string> Names(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var item in File.Value[kind]!.AsArray())
        {
            data.Add(item!["name"]!.GetValue<string>());
        }

        return data;
    }

    public static ConversionCase Load(string kind, string name)
    {
        var item = File.Value[kind]!.AsArray().Single(candidate => candidate!["name"]!.GetValue<string>() == name)!;
        return new ConversionCase(
            name,
            item["markdown"]?.GetValue<string>(),
            item["html"]?.GetValue<string>(),
            [.. item["notes"]!.AsArray().Select(note => note!.GetValue<string>())],
            item["expected"]!.DeepClone());
    }

    /// <summary>The expected document as the converter writes it: no <c>content</c> at all for an empty note.</summary>
    public JsonNode ExpectedDocument()
    {
        var document = new JsonObject { ["type"] = "doc" };
        if (Expected.AsArray().Count > 0)
        {
            document["content"] = Expected.DeepClone();
        }

        return document;
    }

    public static string[] NoteStrings(IEnumerable<ConversionNote> notes) =>
        [.. notes.Select(note => note.Detail is null ? note.Code : $"{note.Code}:{note.Detail}").Order(StringComparer.Ordinal)];

    public static string Pretty(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
