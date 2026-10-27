using System.Diagnostics;
using System.Text.Json.Nodes;
using Inkpact.Api.Crdt;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Crdt;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// Content made to get something past the conversion: script, handlers, addresses that run code, ids of other people's
/// files, and nesting that is meant to use up time or stack. Whatever the input, the document has to be made of the editor's
/// own nodes, with links that pass the client's rule, quickly enough, and the real sidecar has to accept it.
/// </summary>
[Collection(YjsCollection.Name)]
public sealed class HostileContentTests(YjsSidecarFixture yjs)
{
    private const string AttachmentId = "3f2b8c1e-9d4a-4e6b-8a57-0c1d2e3f4a5b";

    /// <summary>Every attribute name the editor's nodes and marks have. Anything else in a document is something the converter let through.</summary>
    private static readonly HashSet<string> KnownAttributes = new(StringComparer.Ordinal)
    {
        "level", "start", "type", "language", "checked", "colspan", "rowspan", "colwidth", "align", "attachmentId", "alt",
        "fileName", "size", "href", "title", "target", "rel", "class",
    };

    public static TheoryData<string, bool, string> Inputs => new()
    {
        { "script element", true, "<script>alert(1)</script><p>x</p>" },
        { "event handler on an image", true, "<img src=x onerror=alert(1)>text" },
        { "javascript link", true, "<a href=\"javascript:alert(1)\">x</a>" },
        { "javascript link with an entity", true, "<a href=\"&#106;avascript:alert(1)\">x</a>" },
        { "javascript link with a tab written as an entity", true, "<a href=\"java&#x09;script:alert(1)\">x</a>" },
        { "javascript link in capitals with a leading control character", true, "<a href=\"&#1;JAVASCRIPT:alert(1)\">x</a>" },
        { "data link", true, "<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>" },
        { "other schemes", true, "<a href=\"vbscript:x\">a</a><a href=\"file:///etc/passwd\">b</a><a href=\"joplin://x\">c</a><a href=\"blob:x\">d</a>" },
        { "iframe with srcdoc", true, "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>t" },
        { "svg with script and handler", true, "<svg onload=alert(1)><script>alert(1)</script></svg>t" },
        { "mathml with a link", true, "<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>t" },
        { "style with an import", true, "<style>@import url(//evil.example/x.css)</style><p style=\"background:url(javascript:alert(1))\">x</p>" },
        { "form", true, "<form action=\"javascript:alert(1)\"><input name=x><button>go</button></form>" },
        { "meta refresh and base", true, "<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\"><base href=\"//evil.example/\">x" },
        { "object and embed", true, "<object data=\"x\"></object><embed src=\"x\">t" },
        { "attributes on a good link", true, "<a href=\"https://example.com\" onclick=\"alert(1)\" style=\"x\" target=\"_self\" rel=\"opener\">ok</a>" },
        { "attachment ids written in the html", true, $"<img data-attachment-id=\"{AttachmentId}\" src=\"x\"><a data-attachment-id=\"{AttachmentId}\" data-file-name=\"x\" data-size=\"1\" href=\"y\">f</a><span data-type=\"taskItem\">s</span>" },
        { "a huge colspan", true, "<table><tr><td colspan=\"999999999\">x</td></tr><tr><td>y</td></tr></table>" },
        { "a table that pads to millions of cells", true, "<table><tr><td colspan=\"100\">x</td></tr>" + string.Concat(Enumerable.Repeat("<tr><td>y</td></tr>", 5_000)) + "</table>" },
        { "a huge start", true, "<ol start=\"99999999999\"><li>x</li></ol>" },
        { "a language that is markup", true, "<pre class=\"language-<script>\">x</pre>" },
        { "markup deeper than anyone writes", true, string.Concat(Enumerable.Repeat("<div>", 100_000)) + "text" },
        { "deep markup written with slashes the parser ignores", true, string.Concat(Enumerable.Repeat("<div/>", 200_000)) + "text" },
        { "deep markup with a stray closing tag after every element", true, string.Concat(Enumerable.Repeat("<div></i>", 200_000)) + "text" },
        { "deep markup with closing tags the parser ignores", true, string.Concat(Enumerable.Repeat("<span><div></span>", 100_000)) + "text" },
        { "deep markup with comments and scripts left open", true, string.Concat(Enumerable.Repeat("<div>", 3_001)) + string.Concat(Enumerable.Repeat("<!--x<script>y", 50_000)) },
        { "html deep in a markdown list", false, HtmlDeepInAMarkdownList() },
        { "quotes nested 2000 deep", true, string.Concat(Enumerable.Repeat("<blockquote>", 2_000)) + "text" },
        { "lists nested 2000 deep", true, string.Concat(Enumerable.Repeat("<ul><li>", 2_000)) + "text" },
        { "formatting nested 2000 deep", true, string.Concat(Enumerable.Repeat("<b><i>", 2_000)) + "text" },
        { "tables nested 400 deep", true, string.Concat(Enumerable.Repeat("<table><tr><td>", 400)) + "text" },
        { "characters a document cannot hold", true, "<p>a\0b\ud800c\u0001d</p>" },
        { "a lot of line breaks", true, "<p>" + string.Concat(Enumerable.Repeat("<br>", 50_000)) + "x</p>" },
        { "markdown javascript links", false, "[a](javascript:alert(1)) [b]( javascript:alert(1)) [c](<javascript:alert(1)>) <javascript:alert(1)> ![d](javascript:alert(1))" },
        { "markdown javascript link by reference", false, "[x][1]\n\n[1]: javascript:alert(1)" },
        { "markdown javascript link with entities", false, "[a](&#106;avascript:alert(1)) [b](JaVaScRiPt&colon;alert(1))" },
        { "markdown with html handlers", false, "<img src=x onerror=alert(1)> <a href=\"javascript:alert(1)\">x</a> <span onmouseover=alert(1)>y</span>" },
        { "markdown with a script across lines", false, "a <script>\nalert(1)\n</script> b" },
        { "markdown with a huge address", false, "[x](https://example.com/" + "a" + "/)" },
        { "markdown quotes nested 5000 deep", false, new string('>', 5_000) + " text" },
        { "markdown lists nested 2000 deep", false, string.Concat(Enumerable.Repeat("- ", 2_000)) + "text" },
        { "markdown emphasis nested 5000 deep", false, string.Concat(Enumerable.Repeat("*a **a ", 5_000)) + "text" },
        { "markdown brackets", false, string.Concat(Enumerable.Repeat("[", 2_000)) + "x" + string.Concat(Enumerable.Repeat("](https://example.com)", 2_000)) },
        { "markdown with characters a document cannot hold", false, "a\0b\ud800c\u0001d" },
        { "markdown uppercase resource ids", false, "[x](:/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA) ![y](:/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB)" },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public async Task Whatever_the_input_the_document_is_made_of_the_editors_own_nodes_and_the_sidecar_takes_it(
        string name, bool isHtml, string body)
    {
        var watch = Stopwatch.StartNew();

        var converted = JoplinContent.Convert(body, isHtml, ConversionCase.Resources);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), name);
        var document = converted.Document.ToJson();
        Validate(document, 0);

        // The sidecar refuses any node, mark or attribute the editor does not have: the last word on what is allowed.
        var update = await yjs.Sidecar.BuildAsync(converted.Document.ToUtf8Json(), default);
        update.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_huge_address_is_not_a_link_and_the_text_stays()
    {
        var converted = JoplinContent.Convert($"[the text]({"https://example.com/" + new string('a', 100_000)})", isHtml: false, NoResources.Instance);

        converted.Document.ToJson().ToJsonString().ShouldContain("the text");
        converted.Document.ToJson().ToJsonString().ShouldNotContain("\"marks\"");
        ConversionCase.NoteStrings(converted.Notes).ShouldBe(["link_removed:https"]);
    }

    [Fact]
    public void Markup_too_deep_to_parse_keeps_its_text_and_says_so()
    {
        var converted = JoplinContent.Convert("<div>" + string.Concat(Enumerable.Repeat("<span>", 4_000)) + "keep &amp; me<script>evil()</script>", isHtml: true, NoResources.Instance);

        var document = converted.Document.ToJson();
        document["content"]![0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("keep & me");
        ConversionCase.NoteStrings(converted.Notes).ShouldBe(["nesting_flattened:html"]);
    }

    [Theory]
    [InlineData("<div/>")]
    [InlineData("<div></i>")]
    [InlineData("<span><div></span>")]
    public void Markup_that_only_looks_shallow_is_not_parsed_either(string piece)
    {
        var converted = JoplinContent.Convert(string.Concat(Enumerable.Repeat(piece, ContentRules.MaxMarkupDepth + 1)) + "text", isHtml: true, NoResources.Instance);

        ConversionCase.NoteStrings(converted.Notes).ShouldBe(["nesting_flattened:html"]);
    }

    /// <summary>A list nested as deep as the editor gets lists, with HTML tables nested inside its deepest item.</summary>
    private static string HtmlDeepInAMarkdownList()
    {
        var markdown = new System.Text.StringBuilder();
        for (var level = 0; level < ContentRules.MaxBlockNesting; level++)
        {
            markdown.Append(' ', 2 * level).Append("- item\n");
        }

        return markdown.Append('\n').Append(' ', 2 * ContentRules.MaxBlockNesting)
            .Append(string.Concat(Enumerable.Repeat("<table><tr><td>", 12))).Append("deep").Append(string.Concat(Enumerable.Repeat("</td></tr></table>", 12)))
            .Append('\n').ToString();
    }

    [Fact]
    public async Task Html_in_markdown_counts_the_blocks_the_markdown_is_already_in()
    {
        var converted = JoplinContent.Convert(HtmlDeepInAMarkdownList(), isHtml: false, NoResources.Instance);

        converted.Document.ToJson().ToJsonString().ShouldContain("\"deep\"");
        ConversionCase.NoteStrings(converted.Notes).ShouldBe(["nesting_flattened:table"]);
        (await yjs.Sidecar.BuildAsync(converted.Document.ToUtf8Json(), default)).ShouldNotBeEmpty();
    }

    [Fact]
    public void Only_a_resource_the_export_has_can_become_an_attachment()
    {
        var converted = JoplinContent.Convert(
            $"![x](:/{new string('f', 32)}) <img src=\":/{new string('f', 32)}\"> <img data-attachment-id=\"{AttachmentId}\" src=\"{AttachmentId}\">",
            isHtml: false,
            ConversionCase.Resources);

        converted.Document.ToJson().ToJsonString().ShouldNotContain(AttachmentId);
    }

    [Fact]
    public void A_link_to_a_file_names_the_file_by_what_the_export_says_not_by_what_the_note_says()
    {
        var converted = JoplinContent.Convert("[<b>evil.exe</b>](:/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb)", isHtml: false, ConversionCase.Resources);

        var file = converted.Document.ToJson()["content"]![0]!["content"]![0]!;
        file["attrs"]!["fileName"]!.GetValue<string>().ShouldBe("dummy.pdf");
    }

    /// <summary>Nothing in the document is outside what the converter is meant to make.</summary>
    private static void Validate(JsonNode node, int depth)
    {
        depth.ShouldBeLessThan(60, "nested deeper than the sidecar follows");
        var type = node["type"]!.GetValue<string>();
        type.ShouldNotBeNullOrWhiteSpace();

        if (node["attrs"] is JsonObject attributes)
        {
            foreach (var (name, value) in attributes)
            {
                KnownAttributes.ShouldContain(name, $"attribute {name} on {type}");
                if (name == "href")
                {
                    ContentRules.AllowedLink(value!.GetValue<string>(), out _).ShouldNotBeNull($"link {value}");
                }
            }
        }

        if (node["text"] is { } text)
        {
            var value = text.GetValue<string>();
            value.ShouldNotBeEmpty();
            // Round-tripping through UTF-8 changes a string that has half a surrogate pair in it.
            System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(value)).ShouldBe(value);
            value.Where(char.IsControl).ShouldAllBe(c => c == '\n' || c == '\t');
        }

        foreach (var mark in node["marks"]?.AsArray() ?? [])
        {
            Validate(mark!, depth + 1);
        }

        foreach (var child in node["content"]?.AsArray() ?? [])
        {
            Validate(child!, depth + 1);
        }
    }
}
