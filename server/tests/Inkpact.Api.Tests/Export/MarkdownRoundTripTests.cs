using System.Text.Json.Nodes;
using Inkpact.Api.Export;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Import;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Export;

/// <summary>
/// The 1.18 row's round trip: a page written as Markdown and read back by the import's own converter is the same document.
/// Pictures, files and links between pages are written as <c>:/id</c>, which the import maps back (a zip's relative paths are
/// the export's, run 1.18b). "The same" is <see cref="DocumentNormal"/>: what Markdown cannot carry is not compared.
/// </summary>
public class MarkdownRoundTripTests(ITestOutputHelper output)
{
    internal static readonly ExportLinks Links = new(
        attachment => ConversionCase.ResourceIdOf(attachment) is { } id ? ":/" + id : null,
        page => ConversionCase.PageIdOf(page) is { } id ? ":/" + id : null);

    /// <summary>The document as Markdown and back, in the shape two equal documents share.</summary>
    internal static (string Markdown, JsonNode Back) RoundTrip(JsonNode document, IResourceResolver? resources = null)
    {
        var markdown = new MarkdownWriter(Links).Write(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        var back = JoplinContent.Convert(markdown, isHtml: false, resources ?? ConversionCase.Resources).Document.ToJson();
        return (markdown, DocumentNormal.Of(back));
    }

    public static TheoryData<string> MarkdownCases => ConversionCase.MarkdownNames;

    public static TheoryData<string> HtmlCases => ConversionCase.HtmlNames;

    /// <summary>
    /// Cases of the import's own fixtures whose document Markdown cannot say as it is, each with why. Everything else must
    /// come back equal.
    /// </summary>
    private static readonly Dictionary<string, string> Different = new()
    {
        ["a heading with a break"] = "A heading is one line in Markdown: its break is a space (MarkdownWriterTests checks that).",
    };

    [Theory]
    [MemberData(nameof(MarkdownCases))]
    public void What_the_import_made_of_Markdown_is_written_and_read_back_the_same(string name) => Check("markdown", name);

    [Theory]
    [MemberData(nameof(HtmlCases))]
    public void What_the_import_made_of_HTML_is_written_and_read_back_the_same(string name) => Check("html", name);

    private void Check(string kind, string name)
    {
        var fixture = ConversionCase.Load(kind, name);
        var expected = fixture.ExpectedDocument();
        if (Different.TryGetValue(name, out var why))
        {
            output.WriteLine($"{name}: {why}");
            return;
        }

        var (markdown, back) = RoundTrip(expected);

        DocumentNormal.Of(expected).ToJsonString().ShouldBe(back.ToJsonString(), $"{name}\n--- Markdown ---\n{markdown}");
    }
}
