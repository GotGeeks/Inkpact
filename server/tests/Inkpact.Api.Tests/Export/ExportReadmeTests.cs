using Inkpact.Api.Export;

namespace Inkpact.Api.Tests.Export;

public sealed class ExportReadmeTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("nl", "nl")]
    [InlineData("NL", "nl")]
    [InlineData("de", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void The_language_is_the_one_asked_for_or_English(string? requested, string expected) =>
        ExportReadme.LanguageOf(requested).ShouldBe(expected);

    [Fact]
    public void The_README_says_what_this_is_in_English()
    {
        var text = ExportReadme.Write("en", "Private", "2026-11-04", 12, 3, [], []);

        text.ShouldStartWith("# Inkpact export\n");
        text.ShouldContain("“Private”");
        text.ShouldContain("2026-11-04");
        text.ShouldContain("Notes: 12. Files: 3.");
        text.ShouldContain("_attachments");
        text.ShouldNotContain("{pages}");
    }

    [Fact]
    public void The_README_is_in_Dutch_for_Dutch()
    {
        var text = ExportReadme.Write("nl", "Privé", "2026-11-04", 1, 0, [], []);

        text.ShouldStartWith("# Inkpact-export\n");
        text.ShouldContain("Notities: 1. Bestanden: 0.");
    }

    [Fact]
    public void Files_and_pages_that_could_not_be_read_are_listed_and_otherwise_there_is_no_such_section()
    {
        ExportReadme.Write("en", "W", "2026-11-04", 1, 0, [], []).ShouldNotContain("could not be read");

        var text = ExportReadme.Write("en", "W", "2026-11-04", 1, 0, ["_attachments/a`b.pdf"], ["Notes/Broken.md"]);

        text.ShouldContain("- ``_attachments/a`b.pdf``");
        text.ShouldContain("- `Notes/Broken.md`");
    }

    [Theory]
    [InlineData("_attachments/a``b", "- ```_attachments/a``b```")]
    [InlineData("`start", "- `` `start ``")]
    [InlineData("end`", "- `` end` ``")]
    [InlineData("plain.pdf", "- `plain.pdf`")]
    public void A_listed_path_is_one_code_span_whatever_backticks_it_has(string path, string expected) =>
        ExportReadme.Write("en", "W", "2026-11-04", 1, 0, [path], []).ShouldContain(expected + "\n");

    [Fact]
    public void Markdown_and_HTML_in_the_workspace_name_are_shown_as_written_not_rendered()
    {
        var text = ExportReadme.Write("en", "Team <img src=x> **bold** [link](x)", "2026-11-04", 1, 0, [], []);

        text.ShouldContain(@"Team \<img src=x> \*\*bold\*\* \[link\](x)");
        text.ShouldNotContain(" <img");
    }

    [Fact]
    public void A_workspace_name_with_braces_is_not_read_as_a_placeholder() =>
        ExportReadme.Write("en", "{date} {pages}", "2026-11-04", 7, 0, [], []).ShouldContain("“{date} {pages}”");
}
