using Inkpact.Api.Export;

namespace Inkpact.Api.Tests.Export;

public sealed class ExportFrontMatterTests
{
    private static readonly Guid Id = Guid.Parse("0197a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly DateTimeOffset Created = new(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Updated = new(2026, 3, 5, 5, 6, 7, TimeSpan.Zero);

    private static ExportPage Page(string title = "A title", string[]? tags = null, bool todo = false, DateTimeOffset? completed = null, DateTimeOffset? due = null) =>
        new(Id, null, title, "a0", Created, Updated, todo, completed, due, tags ?? []);

    [Fact]
    public void A_page_has_its_title_id_and_dates_in_UTC() =>
        ExportFrontMatter.Of(Page()).ShouldBe(
            "---\ntitle: \"A title\"\nid: \"0197a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b\"\ncreated: 2026-03-04T03:06:07Z\nupdated: 2026-03-05T05:06:07Z\n---\n");

    [Fact]
    public void Tags_are_a_list_when_there_are_any()
    {
        ExportFrontMatter.Of(Page(tags: ["work", "to read"])).ShouldContain("tags: [\"work\", \"to read\"]\n");
        ExportFrontMatter.Of(Page()).ShouldNotContain("tags");
    }

    [Fact]
    public void A_todo_has_its_state_and_the_dates_that_are_set()
    {
        var open = ExportFrontMatter.Of(Page(todo: true));
        open.ShouldContain("todo: true\n");
        open.ShouldNotContain("completed");
        open.ShouldNotContain("due");

        var done = ExportFrontMatter.Of(Page(todo: true, completed: Updated, due: Created));
        done.ShouldContain("completed: 2026-03-05T05:06:07Z\n");
        done.ShouldContain("due: 2026-03-04T03:06:07Z\n");
    }

    [Fact]
    public void A_page_that_is_no_todo_has_no_todo_fields()
    {
        // The row can still carry dates after a to-do is turned into a note; they are not part of a note.
        var text = ExportFrontMatter.Of(Page(todo: false, completed: Updated, due: Created));

        text.ShouldNotContain("todo");
        text.ShouldNotContain("completed");
        text.ShouldNotContain("due");
    }

    [Theory]
    [InlineData("He said \"hi\"", "title: \"He said \\\"hi\\\"\"\n")]
    [InlineData("back\\slash", "title: \"back\\\\slash\"\n")]
    [InlineData("colon: and # hash", "title: \"colon: and # hash\"\n")]
    [InlineData("line\nbreak", "title: \"line\\nbreak\"\n")]
    [InlineData("---", "title: \"---\"\n")]
    [InlineData("Ünï ✓ 日本", "title: \"Ünï ✓ 日本\"\n")]
    [InlineData("", "title: \"\"\n")]
    [InlineData("🚀 Launch", "title: \"🚀 Launch\"\n")]
    [InlineData("a\x2028b\u0085c\u0001d\u007Fe", "title: \"a\\u2028b\\u0085c\\u0001d\\u007Fe\"\n")]
    [InlineData("tab\there", "title: \"tab\\there\"\n")]
    public void A_title_cannot_break_out_of_its_line(string title, string expected) =>
        ExportFrontMatter.Of(Page(title)).ShouldContain(expected);

    [Fact]
    public void Half_of_a_character_outside_the_basic_plane_is_the_replacement_character()
    {
        // Built here: a test's data cannot carry a lone surrogate through xUnit unharmed.
        var title = "half " + (char)0xD83D;

        ExportFrontMatter.Of(Page(title)).ShouldContain("title: \"half " + (char)0xFFFD + "\"" + '\n');
    }

    [Fact]
    public void The_front_matter_is_one_block_between_two_lines_of_dashes()
    {
        var lines = ExportFrontMatter.Of(Page(title: "x\ny: z\n---\nz", tags: ["a\n---"])).Split('\n');

        lines[0].ShouldBe("---");
        lines[^2].ShouldBe("---");
        lines.Count(l => l == "---").ShouldBe(2);
    }
}
