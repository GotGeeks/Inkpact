using Inkpact.Api.Import;
using Inkpact.Api.Search;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Search;

public class NoteFileTextTests
{
    [Fact]
    public void Markdown_becomes_its_words()
    {
        var text = NoteFileText.Of("# Title\n\nSome **bold** and *slanted* words, a [link](https://example.com/x) and `code`.\n\n- one\n- two\n", isHtml: false);

        text.ShouldContain("Title");
        text.ShouldContain("Some bold and slanted words, a link and code.");
        text.ShouldContain("one");
        text.ShouldNotContain("**");
        text.ShouldNotContain("#");
        text.ShouldNotContain("example.com");
    }

    [Fact]
    public void A_table_keeps_the_words_of_its_cells()
    {
        var text = NoteFileText.Of("| name | amount |\n| --- | --- |\n| rent | 900 |\n", isHtml: false);

        text.ShouldContain("rent");
        text.ShouldContain("900");
        text.ShouldNotContain("---");
    }

    [Fact]
    public void Html_becomes_its_words_with_entities_decoded_and_scripts_and_styles_dropped_with_their_content()
    {
        var text = NoteFileText.Of("<style>p { color: red }</style><p>Fish &amp; chips <b>today</b></p><script>alert(1)</script>", isHtml: true);

        text.ShouldBe("Fish & chips today");
    }

    [Fact]
    public void Control_characters_and_nul_are_left_out()
    {
        NoteFileText.Of("a\0b\u0001c d", isHtml: false).ShouldBe("abc d");
    }

    [Fact]
    public void A_long_text_is_cut_at_the_most_that_is_indexed_without_splitting_a_character_or_leaving_half_of_one()
    {
        // The pair falls on the cut: half of it is not kept, and nothing is put in its place.
        var text = NoteFileText.Of(new string('a', SearchService.IndexedText - 1) + "\U00020000 tail", isHtml: false);

        text.Length.ShouldBe(SearchService.IndexedText - 1);
        text.ShouldNotContain('�');
        char.IsSurrogate(text[^1]).ShouldBeFalse();
    }

    [Fact]
    public void A_picture_in_the_note_as_a_data_address_is_no_text_and_the_words_after_it_are_kept()
    {
        var picture = "data:image/png;base64," + new string('A', 5_000_000);
        var markdown = NoteFileText.Of($"Before ![a picture]({picture}) after\n\nThe quarterly budget review.", isHtml: false);
        var html = NoteFileText.Of($"<p>Before <img src=\"{picture}\"> after</p><p>The quarterly budget review.</p>", isHtml: true);

        markdown.ShouldContain("The quarterly budget review.");
        markdown.ShouldNotContain("AAAA");
        html.ShouldContain("The quarterly budget review.");
        html.ShouldNotContain("AAAA");
    }

    [Fact]
    public void Markup_inside_a_word_leaves_the_word_whole_and_a_block_never_runs_into_the_next()
    {
        NoteFileText.Of("an ex**am**ple", isHtml: false).ShouldBe("an example");
        NoteFileText.Of("<p>an ex<b>am</b>ple</p><p>next</p><ul><li>one</li><li>two</li></ul>", isHtml: true)
            .ShouldBe("an example\n\nnext\n\none\n\ntwo");
    }

    [Fact]
    public void The_html_in_a_markdown_note_keeps_its_words()
    {
        var text = NoteFileText.Of("Clipped:\n\n<table><tr><td>invoice 4711</td></tr></table>\n\n<div>\nmeeting notes\n</div>\n", isHtml: false);

        text.ShouldContain("invoice 4711");
        text.ShouldContain("meeting notes");
        text.ShouldNotContain("<");
    }

    [Fact]
    public void Task_lists_footnotes_and_maths_are_read_as_the_converter_reads_them()
    {
        var text = NoteFileText.Of("- [x] pay the rent\n- [ ] call home\n\nA claim[^1].\n\n[^1]: The source.\n\n$$x^2$$\n", isHtml: false);

        text.ShouldContain("pay the rent");
        text.ShouldContain("call home");
        text.ShouldContain("The source.");
        text.ShouldNotContain("[x]");
        text.ShouldNotContain("[^1]");
    }

    [Fact]
    public void The_white_space_of_markup_is_made_small_so_it_does_not_use_up_what_is_kept()
    {
        var text = NoteFileText.Of("<div>\n        <p>\n            one     two\n        </p>\n\n\n\n        <p>three</p>\n</div>", isHtml: true);

        text.ShouldBe("one two\n\nthree");
    }

    [Fact]
    public void A_note_that_is_nothing_but_markup_has_no_text()
    {
        NoteFileText.Of("<img src=\"x\"><br>", isHtml: true).ShouldBe("");
        NoteFileText.Of("", isHtml: false).ShouldBe("");
    }

    [Fact]
    public void A_list_nested_far_deeper_than_anyone_writes_still_gives_its_words()
    {
        var body = string.Concat(Enumerable.Range(0, 500).Select(i => new string(' ', i * 2) + "- level " + i + "\n"));

        var text = NoteFileText.Of(body, isHtml: false);

        text.ShouldContain("level 0");
    }
}

/// <summary>The column and the vector that is made from it, against the real database.</summary>
public class FileTextColumnTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> AddPageAsync(string title, string content, string fileText)
    {
        await using var db = CreateContext();
        var user = await db.Users.FirstOrDefaultAsync() ?? new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Now };
        var workspace = await db.Workspaces.FirstOrDefaultAsync() ?? new Workspace { Name = "Workspace", OwnerId = user.Id, CreatedAt = Now };
        if (db.Entry(user).State == EntityState.Detached)
        {
            db.Add(user);
        }

        if (db.Entry(workspace).State == EntityState.Detached)
        {
            db.Add(workspace);
        }

        var page = new Page { WorkspaceId = workspace.Id, Title = title, SortKey = $"a{Guid.NewGuid():N}"[..20], CreatedAt = Now, UpdatedAt = Now, ContentText = content, FileText = fileText };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    private async Task<bool> MatchesAsync(Guid pageId, string query)
    {
        await using var db = CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pages WHERE id = {pageId} AND search_vector @@ to_tsquery('inkpact_search', {query})")
            .SingleAsync() == 1;
    }

    [Fact]
    public async Task The_vector_is_made_from_the_content_and_from_the_file_text()
    {
        var page = await AddPageAsync("Big", "Big.md", "the quarterly budget review");

        (await MatchesAsync(page, "quarter:*")).ShouldBeTrue();
        (await MatchesAsync(page, "big:*")).ShouldBeTrue();
        (await MatchesAsync(page, "zeppelin:*")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_page_with_the_most_content_text_and_the_most_file_text_does_not_overflow_the_vector()
    {
        // Words that are all different: the whole of both would be more than the 1 MB a tsvector holds.
        var content = string.Join(' ', Enumerable.Range(0, 200_000).Select(i => $"c{i:x}"));
        var file = string.Join(' ', Enumerable.Range(0, 200_000).Select(i => $"f{i:x}"));

        var page = await AddPageAsync("Both", content, file);

        (await MatchesAsync(page, "c5:*")).ShouldBeTrue();
        // What is past the first 200,000 characters of the two together is not indexed, so the file's words come after the content's.
        (await MatchesAsync(page, "f5:*")).ShouldBeFalse();
    }

    [Fact]
    public async Task The_text_of_a_file_is_found_when_the_content_is_only_its_name()
    {
        var page = await AddPageAsync("Big", "Big.md", string.Join(' ', Enumerable.Range(0, 30_000).Select(i => $"w{i:x}")));

        (await MatchesAsync(page, "w5:*")).ShouldBeTrue();
        (await MatchesAsync(page, "w752f:*")).ShouldBeTrue();
    }
}
