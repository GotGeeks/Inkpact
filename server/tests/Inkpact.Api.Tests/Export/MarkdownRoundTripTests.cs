using System.Text.Json.Nodes;
using Inkpact.Api.Export;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Import;
using Xunit.Abstractions;
using static Inkpact.Api.Tests.Export.Pm;

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

    private static readonly Guid FileId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    /// <summary>Shapes that neither the fixtures nor the random documents have, found by reading the writer.</summary>
    private static readonly (string Name, JsonNode Document)[] Shapes =
    [
        ("a heading after a break and white space", Doc(P(T("a"), Br(), T("  # b")))),
        ("a list after a break and white space", Doc(P(T("a"), Br(), T("  - b")))),
        ("a quote after a break and white space", Doc(P(T("a"), Br(), T("   > b")))),
        ("a list marker with a tab after it", Doc(P(T("a"), Br(), T("-\tb")))),
        ("a number with a tab after it", Doc(P(T("a"), Br(), T("1.\tb")))),
        ("a file after a bang", Doc(P(T("Look!"), File(FileId, "dummy.pdf", 12345)))),
        ("code with a pipe in a table", Doc(Table(Row(Cell(true, P(T("h")))), Row(Cell(false, P(T("a|b", "code"))))))),
        ("a link with a pipe in a table", Doc(Table(Row(Cell(true, P(T("h")))), Row(Cell(false, P(Link("x", "https://x.org/?q=a|b"))))))),
        ("a link title with a pipe in a table", Doc(Table(Row(Cell(true, P(T("h")))), Row(Cell(false, P(Link("x", "https://x.org/", "a|b"))))))),
        ("a heading that is a hash", Doc(H(1, T("#")))),
        ("a heading that ends in hashes after a space", Doc(H(2, T("a ##")))),
        ("a bullet list and a task list", Doc(Ul(Li(P(T("a")))), Node("taskList", null, Task(false, P(T("b")))))),
        ("three bullet lists", Doc(Ul(Li(P(T("a")))), Ul(Li(P(T("b")))), Ul(Li(P(T("c")))))),
        ("two numbered lists", Doc(Ol(1, Li(P(T("a")))), Ol(1, Li(P(T("b")))))),
        ("lists that touch once an empty paragraph is left out", Doc(Ul(Li(P(T("a")))), P(), Ul(Li(P(T("b")))))),
        ("lists that touch in a list item", Doc(Ul(Li(P(T("a")), Ul(Li(P(T("b")))), Ul(Li(P(T("c")))))))),
        ("code with blank lines in an HTML table", Doc(Table(Row(Cell(false, Code(null, "a\n\n\nb\n \nc")))))),
        ("code in a table in a table", Doc(Table(Row(Cell(false, Table(Row(Cell(false, Code(null, "line1\nline2"))))))))),
        ("an address with mailto in text", Doc(P(T("write to mailto:me@x.org")))),
        ("an address with tel in text", Doc(P(T("call tel:+31123456")))),
        ("an empty task item", Doc(Node("taskList", null, Task(true), Task(false, P(T("b")))))),
    ];

    public static TheoryData<int> ShapeNumbers => [.. Enumerable.Range(0, Shapes.Length)];

    [Theory]
    [MemberData(nameof(ShapeNumbers))]
    public void A_shape_markdown_reads_another_way_unless_written_with_care_comes_back_the_same(int number)
    {
        var (name, document) = Shapes[number];

        var (markdown, back) = RoundTrip(document);

        var before = DocumentNormal.Of(document);
        JsonNode.DeepEquals(before, back).ShouldBeTrue($"{name}\n--- Markdown ---\n{markdown}\n{DocumentNormal.FirstDifference(before, back)}");
    }

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
