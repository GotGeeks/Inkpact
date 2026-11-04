using System.Text.Json.Nodes;
using Inkpact.Api.Crdt;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Export;

/// <summary>
/// A page as the editor really writes it (the fixtures are real Yjs updates, made by typing and formatting), read by the
/// sidecar, written as Markdown and read back by the import: the same document. The whole way a page is exported, but for the zip.
/// </summary>
[Collection(YjsCollection.Name)]
public sealed class ExportOfEditedPagesTests(YjsSidecarFixture yjs)
{
    [Theory]
    [MemberData(nameof(YjsFixture.Names), MemberType = typeof(YjsFixture))]
    public async Task A_page_the_editor_wrote_is_exported_and_read_back_as_the_same_document(string name)
    {
        var read = await ((IYjsCodec)yjs.Sidecar).ReadAsync(YjsFixture.Load(name).UpdateBytes, default);
        var document = JsonNode.Parse(read.Json)!;

        var (markdown, back) = MarkdownRoundTripTests.RoundTrip(document);

        var before = DocumentNormal.Of(document);
        JsonNode.DeepEquals(before, back).ShouldBeTrue($"{name}\n{DocumentNormal.FirstDifference(before, back)}");
    }
}
