using System.Text;
using Inkpact.Api.Crdt;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Import;

/// <summary>The conversion's output given to the real sidecar's build operation, the way the import job will.</summary>
[Collection(YjsCollection.Name)]
public sealed class SidecarBuildTests(YjsSidecarFixture yjs)
{
    private IYjsCodec Codec => yjs.Sidecar;

    [Theory]
    [MemberData(nameof(ConversionCase.MarkdownNames), MemberType = typeof(ConversionCase))]
    public async Task Every_markdown_fixture_is_built_into_a_page_with_its_text(string name)
    {
        var testCase = ConversionCase.Load("markdown", name);
        var converted = JoplinContent.Convert(testCase.Markdown!, isHtml: false, ConversionCase.Resources);

        await ShouldBuildWithTheTextOf(converted);
    }

    [Theory]
    [MemberData(nameof(ConversionCase.HtmlNames), MemberType = typeof(ConversionCase))]
    public async Task Every_html_fixture_is_built_into_a_page_with_its_text(string name)
    {
        var testCase = ConversionCase.Load("html", name);
        var converted = JoplinContent.Convert(testCase.Html!, isHtml: true, ConversionCase.Resources);

        await ShouldBuildWithTheTextOf(converted);
    }

    private async Task ShouldBuildWithTheTextOf(ConvertedContent converted)
    {
        var update = await Codec.BuildAsync(converted.Document.ToUtf8Json(), default);

        // Read back by the sidecar's own search extract: the text the page would be found by.
        var compaction = await Codec.CompactAsync([update], default);
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
        var expected = TextOf(converted.Document.ToJson());
        YjsFixture.Lines(compaction.Text).ShouldBe(YjsFixture.Lines(expected));
    }

    /// <summary>The text of a document the way the search extract reads it: a line for each block, a line end for each break.</summary>
    private static string TextOf(System.Text.Json.Nodes.JsonNode node)
    {
        var text = new StringBuilder();
        void Walk(System.Text.Json.Nodes.JsonNode current)
        {
            var type = current["type"]!.GetValue<string>();
            if (type == "text")
            {
                text.Append(current["text"]!.GetValue<string>());
                return;
            }

            if (type is "hardBreak" or "image" or "attachment" or "horizontalRule")
            {
                text.Append('\n');
                return;
            }

            var isBlock = type is not "doc";
            if (isBlock)
            {
                text.Append('\n');
            }

            foreach (var child in current["content"]?.AsArray() ?? [])
            {
                Walk(child!);
            }

            if (isBlock)
            {
                text.Append('\n');
            }
        }

        Walk(node);
        return text.ToString();
    }

    [Fact]
    public async Task The_sidecar_refuses_a_node_the_editor_does_not_have()
    {
        var content = """{"type":"doc","content":[{"type":"iframe"}]}"""u8.ToArray();

        await Should.ThrowAsync<YjsRejectedException>(() => Codec.BuildAsync(content, default));
    }

    [Fact]
    public async Task The_sidecar_refuses_content_that_is_not_json()
    {
        await Should.ThrowAsync<YjsRejectedException>(() => Codec.BuildAsync("{"u8.ToArray(), default));
    }

    private static byte[] Json(string markdown) =>
        JoplinContent.Convert(markdown, isHtml: false, NoResources.Instance).Document.ToUtf8Json();

    [Fact]
    public async Task A_page_is_replaced_by_an_update_that_reads_as_the_new_content()
    {
        var old = await Codec.BuildAsync(Json("# Old\n\nold text"), default);
        var converted = JoplinContent.Convert("New *text*\n\n- one\n- two", isHtml: false, NoResources.Instance);

        var replacement = await Codec.ReplaceAsync([old], converted.Document.ToUtf8Json(), default);

        var compaction = await Codec.CompactAsync([old, replacement.Update], default);
        compaction.StateFingerprint.ShouldBe(compaction.ReplayFingerprint);
        // The page after the update, as the sidecar answered it, is the page the update makes.
        replacement.Fingerprint.ShouldBe(compaction.ReplayFingerprint);
        replacement.StateVector.ShouldBe(compaction.StateVector);
        replacement.Text.ShouldBe(compaction.Text);
        YjsFixture.Lines(compaction.Text).ShouldBe(YjsFixture.Lines(TextOf(converted.Document.ToJson())));
        compaction.Text.ShouldNotContain("old");
    }

    [Fact]
    public async Task A_page_is_replaced_from_its_snapshot_and_the_updates_after_it()
    {
        var old = await Codec.BuildAsync(Json("before"), default);
        var snapshot = (await Codec.CompactAsync([old], default)).State;

        var replacement = await Codec.ReplaceAsync([snapshot], Json("after"), default);

        (await Codec.CompactAsync([snapshot, replacement.Update], default)).Text.Trim().ShouldBe("after");
    }

    [Fact]
    public async Task Replacing_with_a_node_the_editor_does_not_have_is_refused()
    {
        var old = await Codec.BuildAsync(Json("before"), default);
        var content = """{"type":"doc","content":[{"type":"iframe"}]}"""u8.ToArray();

        await Should.ThrowAsync<YjsRejectedException>(() => Codec.ReplaceAsync([old], content, default));
    }

    [Fact]
    public async Task An_empty_note_is_the_first_paragraph_and_nothing_else()
    {
        var update = await Codec.BuildAsync(JoplinContent.Convert("", isHtml: false, NoResources.Instance).Document.ToUtf8Json(), default);

        // Client id 1 is the one that makes the first paragraph, and it is the only client in this page.
        var compaction = await Codec.CompactAsync([update], default);
        compaction.Text.ShouldBeEmpty();
        update.ShouldNotBeEmpty();
    }
}
