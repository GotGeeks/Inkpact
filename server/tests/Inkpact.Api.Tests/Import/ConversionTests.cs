using System.Text.Json.Nodes;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

/// <summary>Every row of the conversion table: a Joplin body in, the editor's document and the report's notes out.</summary>
public sealed class ConversionTests
{
    [Theory]
    [MemberData(nameof(ConversionCase.MarkdownNames), MemberType = typeof(ConversionCase))]
    public void Markdown_becomes_the_expected_document(string name)
    {
        var testCase = ConversionCase.Load("markdown", name);

        var converted = JoplinContent.Convert(testCase.Markdown!, isHtml: false, ConversionCase.Resources);

        Check(testCase, converted);
    }

    [Theory]
    [MemberData(nameof(ConversionCase.HtmlNames), MemberType = typeof(ConversionCase))]
    public void Html_becomes_the_expected_document(string name)
    {
        var testCase = ConversionCase.Load("html", name);

        var converted = JoplinContent.Convert(testCase.Html!, isHtml: true, ConversionCase.Resources);

        Check(testCase, converted);
    }

    private static void Check(ConversionCase testCase, ConvertedContent converted)
    {
        var actual = converted.Document.ToJson();
        JsonNode.DeepEquals(testCase.ExpectedDocument(), actual).ShouldBeTrue(
            $"expected{Environment.NewLine}{ConversionCase.Pretty(testCase.ExpectedDocument())}{Environment.NewLine}but got{Environment.NewLine}{ConversionCase.Pretty(actual)}");
        ConversionCase.NoteStrings(converted.Notes).ShouldBe(testCase.Notes.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_same_note_is_counted_instead_of_listed_again()
    {
        var converted = JoplinContent.Convert("<p>a<sup>1</sup> b<sup>2</sup> c<sup>3</sup></p>", isHtml: true, NoResources.Instance);

        converted.Notes.ShouldBe([new ConversionNote(NoteCodes.UnsupportedElement, "sup", 3)]);
    }

    [Fact]
    public void A_description_and_a_link_title_are_cut_at_the_limit_and_the_report_says_so()
    {
        var long1 = new string('a', ContentRules.MaxAltLength + 500);
        var body = $"![{long1}](:/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa)\n\n[text](https://example.com \"{long1}\")";

        var converted = JoplinContent.Convert(body, isHtml: false, ConversionCase.Resources);

        var content = converted.Document.ToJson()["content"]!.AsArray();
        content[0]!["attrs"]!["alt"]!.GetValue<string>().Length.ShouldBe(ContentRules.MaxAltLength);
        content[1]!["content"]![0]!["marks"]![0]!["attrs"]!["title"]!.GetValue<string>().Length.ShouldBe(ContentRules.MaxTitleLength);
        ConversionCase.NoteStrings(converted.Notes).ShouldBe(["truncated:alt", "truncated:title"]);
    }

    [Fact]
    public void A_removed_element_without_content_does_not_take_the_rest_of_the_paragraph_with_it()
    {
        var converted = JoplinContent.Convert(
            "Watch this <embed src=\"x.mp4\"> and remember to call <source src=x>Bob, [here](https://example.com).", isHtml: false, NoResources.Instance);

        var paragraph = converted.Document.ToJson()["content"]![0]!["content"]!.AsArray();
        string.Concat(paragraph.Select(node => node!["text"]!.GetValue<string>())).ShouldBe("Watch this  and remember to call Bob, here.");
        paragraph[1]!["marks"]![0]!["attrs"]!["href"]!.GetValue<string>().ShouldBe("https://example.com");
        ConversionCase.NoteStrings(converted.Notes).ShouldBe(["removed_element:embed"]);
    }

    [Theory]
    [InlineData("embed")]
    [InlineData("iframe")]
    [InlineData("source")]
    [InlineData("track")]
    [InlineData("param")]
    [InlineData("noframes")]
    [InlineData("style")]
    public void A_removed_element_is_reported_the_same_in_markdown_and_in_html(string tag)
    {
        var inMarkdown = JoplinContent.Convert($"a <{tag}> b", isHtml: false, NoResources.Instance);
        var inHtml = JoplinContent.Convert($"<p>a <{tag}> b</p>", isHtml: true, NoResources.Instance);

        ConversionCase.NoteStrings(inMarkdown.Notes).ShouldBe(ConversionCase.NoteStrings(inHtml.Notes));
    }

    [Fact]
    public void Converting_twice_gives_the_same_document()
    {
        const string body = "# T\n\n- [ ] a\n- b\n\n| x | y |\n| - | - |\n| 1 | 2 |\n\n[l](https://e.com) ![i](:/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa)";

        var first = JoplinContent.Convert(body, isHtml: false, ConversionCase.Resources).Document.ToJson().ToJsonString();
        var second = JoplinContent.Convert(body, isHtml: false, ConversionCase.Resources).Document.ToJson().ToJsonString();

        second.ShouldBe(first);
    }
}
