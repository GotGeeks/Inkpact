using Inkpact.Api.Export;

namespace Inkpact.Api.Tests.Export;

public sealed class ExportNamesTests
{
    [Theory]
    [InlineData("Plain title", "Plain title")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a-b-c-d-e-f-g-h-i-j")]
    [InlineData("line\nbreak\ttab", "line-break-tab")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("Ends with space   ", "Ends with space")]
    [InlineData(".hidden", "hidden")]
    [InlineData(". .notes", "notes")]
    [InlineData(" . . x . . ", "x")]
    [InlineData("..  ..hidden.. .", "hidden")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", ExportNames.Untitled)]
    [InlineData("   ", ExportNames.Untitled)]
    [InlineData("...", ExportNames.Untitled)]
    [InlineData(null, ExportNames.Untitled)]
    [InlineData("Nederlands: één, ça, 日本語", "Nederlands- één, ça, 日本語")]
    public void A_title_becomes_a_name_valid_everywhere(string? title, string expected) =>
        ExportNames.Stem(title).ShouldBe(expected);

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("CON.backup", "_CON.backup")]
    [InlineData("Console", "Console")]
    [InlineData("COM10", "COM10")]
    public void Windows_reserved_names_get_an_underscore(string title, string expected) =>
        ExportNames.Stem(title).ShouldBe(expected);

    [Fact]
    public void A_long_title_is_cut_without_splitting_a_character_and_without_a_trailing_dot_or_space()
    {
        ExportNames.Stem(new string('a', 300)).Length.ShouldBe(ExportNames.MaxLength);

        // A character outside the basic plane is two UTF-16 units; the cut must not leave half of one.
        var cut = ExportNames.Stem(string.Concat(Enumerable.Repeat("😀", 80)));
        cut.Length.ShouldBeLessThanOrEqualTo(ExportNames.MaxLength);
        cut.Length.ShouldBeGreaterThan(ExportNames.MaxLength - 2);
        for (var i = 0; i < cut.Length; i++)
        {
            if (char.IsHighSurrogate(cut[i]))
            {
                char.IsLowSurrogate(cut[i + 1]).ShouldBeTrue();
                i++;
            }
            else
            {
                char.IsLowSurrogate(cut[i]).ShouldBeFalse();
            }
        }

        ExportNames.Stem(new string('a', 99) + ". more").ShouldBe(new string('a', 99));
    }

    [Fact]
    public void Text_is_in_one_Unicode_form_so_equal_titles_are_equal_names() =>
        ExportNames.Stem("café").ShouldBe("café");

    [Fact]
    public void A_lone_surrogate_is_a_dash() =>
        ExportNames.Stem("a\uD800b").ShouldBe("a-b");

    [Theory]
    [InlineData("manual.pdf", "manual.pdf")]
    [InlineData("a/b.png", "a-b.png")]
    [InlineData("noextension", "noextension")]
    [InlineData("archive.tar.gz", "archive.tar.gz")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("", ExportNames.Untitled)]
    [InlineData(".png", "png")]
    public void A_file_name_keeps_its_extension(string name, string expected) =>
        ExportNames.FileName(name).ShouldBe(expected);

    [Fact]
    public void A_long_file_name_is_cut_before_its_extension()
    {
        var name = ExportNames.FileName(new string('x', 300) + ".pdf");

        name.Length.ShouldBe(ExportNames.MaxLength);
        name.ShouldEndWith(".pdf");
    }

    [Theory]
    [InlineData("My Notes", "My-Notes")]
    [InlineData("Private", "Private")]
    [InlineData("", "workspace")]
    [InlineData("///", "---")]
    public void The_download_is_named_after_the_workspace(string workspace, string expected) =>
        ExportNames.DownloadName(workspace).ShouldBe(expected);

    [Fact]
    public void Two_pages_with_one_title_get_a_number_in_the_order_they_ask()
    {
        var folder = new ExportNames.Folder();

        folder.TakeStem("Notes").ShouldBe("Notes");
        folder.TakeStem("Notes").ShouldBe("Notes (2)");
        folder.TakeStem("Notes").ShouldBe("Notes (3)");
    }

    [Fact]
    public void Names_that_differ_only_in_case_or_Unicode_form_are_one_name()
    {
        var folder = new ExportNames.Folder();

        folder.TakeStem("Notes").ShouldBe("Notes");
        folder.TakeStem("notes").ShouldBe("notes (2)");
        folder.TakeStem("NOTES").ShouldBe("NOTES (3)");
        folder.TakeStem("café").ShouldBe("café");
        folder.TakeStem("café").ShouldBe("café (2)");
    }

    [Fact]
    public void A_title_that_is_taken_by_a_number_gets_another_number()
    {
        var folder = new ExportNames.Folder();

        folder.TakeStem("Notes");
        folder.TakeStem("Notes");
        folder.TakeStem("Notes (2)").ShouldBe("Notes (2) (2)");
    }

    [Fact]
    public void A_page_called_x_md_does_not_take_the_file_of_a_page_called_x()
    {
        // "X" is the file X.md and the folder X; "X.md" would be the file X.md.md and the folder X.md, which is the file's name.
        var folder = new ExportNames.Folder();
        folder.TakeStem("X").ShouldBe("X");
        folder.TakeStem("X.md").ShouldBe("X.md (2)");

        var other = new ExportNames.Folder();
        other.TakeStem("X.md").ShouldBe("X.md");
        other.TakeStem("X").ShouldBe("X (2)");
    }

    [Fact]
    public void The_number_fits_in_the_length_even_for_a_title_at_the_limit()
    {
        var folder = new ExportNames.Folder();
        var title = new string('a', 300);

        var first = folder.TakeStem(title);
        var second = folder.TakeStem(title);

        first.Length.ShouldBe(ExportNames.MaxLength);
        second.Length.ShouldBeLessThanOrEqualTo(ExportNames.MaxLength);
        second.ShouldEndWith(" (2)");
        second.ShouldNotBe(first);
    }

    [Fact]
    public void Two_different_files_with_one_name_keep_their_extension_when_numbered()
    {
        var folder = new ExportNames.Folder();

        folder.TakeFile("manual.pdf").ShouldBe("manual.pdf");
        folder.TakeFile("manual.pdf").ShouldBe("manual (2).pdf");
        folder.TakeFile("MANUAL.PDF").ShouldBe("MANUAL (3).PDF");
        folder.TakeFile("noextension").ShouldBe("noextension");
        folder.TakeFile("noextension").ShouldBe("noextension (2)");
    }

    [Fact]
    public void A_reserved_name_is_never_given_out()
    {
        var folder = new ExportNames.Folder();
        folder.Reserve("README.md");

        folder.TakeStem("README").ShouldBe("README (2)");
        folder.TakeStem("readme").ShouldBe("readme (3)");
    }
}
