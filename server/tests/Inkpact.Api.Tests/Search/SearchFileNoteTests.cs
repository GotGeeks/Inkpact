using Inkpact.Api.Compaction;
using Inkpact.Api.Import;
using Inkpact.Api.Search;
using Inkpact.Api.Tests.Import;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Search;

/// <summary>
/// A note that the import keeps as a file (run 1.16d) is found by its words (run 1.17c). The limits are set small, so a note
/// of a few kilobytes stands for one of many megabytes, as in <see cref="ImportSizeTests"/>.
/// </summary>
public sealed class SearchFileNoteTests(PostgresFixture postgres) : ReimportHarness(postgres)
{
    private const int TextLimit = 2000;
    private const int UpdateLimit = 3000;

    private Task StartAsync() => StartAsync(new Dictionary<string, string>
    {
        ["Import:MaxNoteTextBytes"] = TextLimit.ToString(),
        ["Import:MaxNoteUpdateBytes"] = UpdateLimit.ToString(),
    });

    /// <summary>Plain filler of about this many bytes, with the given words in the middle.</summary>
    private static string Body(int bytes, string middle = "") =>
        string.Concat(Enumerable.Repeat("filler ", bytes / 14)) + middle + " " + string.Concat(Enumerable.Repeat("filler ", bytes / 14));

    private static JexBuilder Library(string body, bool html = false, string title = "Big") =>
        new JexBuilder().Folder("work").Note("big", "work", body: body, title: title, html: html);

    private async Task<SearchResponse> SearchAsync(string text) =>
        await UseDbAsync(db => new SearchService(db).SearchAsync(WorkspaceId, text));

    private async Task<Guid[]> FindAsync(string text) => [.. (await SearchAsync(text)).Results.Select(r => r.Id)];

    private Task<string> FileTextAsync(Guid pageId) =>
        UseDbAsync(db => db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.FileText).SingleAsync());

    private Task ForgetFileTextAsync(Guid pageId) =>
        UseDbAsync(db => db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(s => s.SetProperty(p => p.FileText, "")));

    [Fact]
    public async Task A_word_from_the_middle_of_a_note_kept_as_a_file_finds_its_page_with_a_snippet()
    {
        await StartAsync();

        await ImportAsync(Library(Body(TextLimit + 500, "the quarterly budget review")));

        var page = await PageOfAsync("big");
        page.ContentText.ShouldNotContain("quarterly");
        var results = (await SearchAsync("quarter")).Results;
        results.Select(r => r.Id).ShouldBe([page.Id]);
        results[0].Snippet.Where(s => s.Match).Select(s => s.Text).ShouldBe(["quarterly"]);
        string.Concat(results[0].Snippet.Select(s => s.Text)).ShouldContain("the quarterly budget review");
    }

    [Fact]
    public async Task The_text_is_plain_text_and_not_the_markup()
    {
        await StartAsync();

        await ImportAsync(Library(Body(TextLimit, "**bold** and [a link](https://example.com/path) here")));

        var page = await PageOfAsync("big");
        var text = await FileTextAsync(page.Id);
        text.ShouldContain("bold and a link here");
        text.ShouldNotContain("**");
        text.ShouldNotContain("example.com");
        (await FindAsync("bold")).ShouldBe([page.Id]);
    }

    [Fact]
    public async Task An_html_note_is_found_by_its_words_and_not_its_tags()
    {
        await StartAsync();

        await ImportAsync(Library($"<p>Begin {Body(TextLimit)} the <b>zeppelin</b> hangar</p>", html: true));

        var page = await PageOfAsync("big");
        (await FindAsync("zeppelin")).ShouldBe([page.Id]);
        (await FindAsync("strong")).ShouldBeEmpty();
        (await FileTextAsync(page.Id)).ShouldNotContain("<");
    }

    [Fact]
    public async Task The_page_is_still_found_after_it_is_compacted_which_rewrites_its_content_text()
    {
        await StartAsync();
        await ImportAsync(Library(Body(TextLimit + 500, "the quarterly budget review")));
        var page = await PageOfAsync("big");

        (await Service<Compactor>().CompactAsync(page.Id, default)).ShouldBe(CompactionResult.Compacted);

        (await FindAsync("quarterly")).ShouldBe([page.Id]);
        (await FileTextAsync(page.Id)).ShouldContain("quarterly budget review");
    }

    [Fact]
    public async Task A_note_that_changes_in_Joplin_is_found_by_its_new_words_and_not_the_old()
    {
        await StartAsync();
        await ImportAsync(Library(Body(TextLimit + 500, "alpha bravo")));
        var page = await PageOfAsync("big");
        (await FindAsync("alpha")).ShouldBe([page.Id]);

        var import = await ImportAsync(Library(Body(TextLimit + 600, "charlie delta")));

        CountsOf(import).Updated.ShouldBe(1);
        (await FindAsync("charlie")).ShouldBe([page.Id]);
        (await FindAsync("alpha")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_note_that_shrinks_back_to_content_keeps_no_file_text_and_is_found_as_content()
    {
        await StartAsync();
        await ImportAsync(Library(Body(TextLimit + 500, "alpha bravo")));
        var page = await PageOfAsync("big");

        await ImportAsync(Library("A short note about charlie"));

        (await FileTextAsync(page.Id)).ShouldBe("");
        (await FindAsync("alpha")).ShouldBeEmpty();
        (await FindAsync("charlie")).ShouldBe([page.Id]);
    }

    [Fact]
    public async Task A_note_that_grows_into_a_file_gets_its_file_text()
    {
        await StartAsync();
        await ImportAsync(Library("A short note"));
        var page = await PageOfAsync("big");

        await ImportAsync(Library(Body(TextLimit + 800, "the zeppelin hangar")));

        (await FindAsync("zeppelin")).ShouldBe([page.Id]);
    }

    [Fact]
    public async Task A_copy_made_because_the_note_changed_in_both_places_is_found_by_its_text_and_the_page_is_left_alone()
    {
        await StartAsync();
        await ImportAsync(Library("A short note"));
        var page = await PageOfAsync("big");
        await PersonEditsAsync(page.Id, "Written here");

        await ImportAsync(Library(Body(TextLimit + 800, "the zeppelin hangar")));

        var copy = await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Title == "Big (Joplin)"));
        (await FindAsync("zeppelin")).ShouldBe([copy.Id]);
        (await FileTextAsync(page.Id)).ShouldBe("");
    }

    [Fact]
    public async Task The_same_export_again_changes_nothing()
    {
        await StartAsync();
        await ImportAsync(Library(Body(TextLimit + 500, "alpha bravo")));
        var page = await PageOfAsync("big");
        var before = await FileTextAsync(page.Id);

        await ImportAsync(Library(Body(TextLimit + 500, "alpha bravo")));

        (await FileTextAsync(page.Id)).ShouldBe(before);
        (await FindAsync("alpha")).ShouldBe([page.Id]);
    }


    // ---- notes kept as files before run 1.17c ---------------------------------------------------------------------------

    /// <summary>What a note imported before run 1.17c is like after the migration: no file text, its item from an older converter.</summary>
    private Task AsBeforeAsync(Guid pageId) => UseDbAsync(async db =>
    {
        await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(s => s.SetProperty(p => p.FileText, ""));
        return await db.ImportItems.Where(i => i.InkpactId == pageId).ExecuteUpdateAsync(s => s.SetProperty(i => i.ConverterVersion, 1));
    });

    [Fact]
    public async Task A_note_kept_as_a_file_before_gets_its_text_at_the_next_import_of_the_same_export()
    {
        await StartAsync();
        var jex = Library(Body(TextLimit + 500, "the quarterly budget review"));
        await ImportAsync(jex);
        var page = await PageOfAsync("big");
        await AsBeforeAsync(page.Id);
        (await FindAsync("quarterly")).ShouldBeEmpty();

        await ImportAsync(jex);

        (await FindAsync("quarterly")).ShouldBe([page.Id]);
        (await ItemOfAsync("big")).ConverterVersion.ShouldBe(ImportOptions.ConverterVersion);
    }

    [Fact]
    public async Task It_gets_the_text_of_the_note_as_Joplin_has_it_then_not_of_an_older_file()
    {
        await StartAsync();
        await ImportAsync(Library(Body(TextLimit + 500, "alpha bravo")));
        await ImportAsync(Library(Body(TextLimit + 600, "charlie delta")));
        var page = await PageOfAsync("big");
        await AsBeforeAsync(page.Id);

        await ImportAsync(Library(Body(TextLimit + 500, "alpha bravo")));

        (await FindAsync("alpha")).ShouldBe([page.Id]);
        (await FindAsync("charlie")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_someone_changed_here_is_left_alone_and_not_converted_again()
    {
        await StartAsync();
        var jex = Library(Body(TextLimit + 500, "alpha bravo"));
        await ImportAsync(jex);
        var page = await PageOfAsync("big");
        await AsBeforeAsync(page.Id);
        await PersonEditsAsync(page.Id, "Written here");

        await ImportAsync(jex);

        (await FileTextAsync(page.Id)).ShouldBe("");
        (await ItemOfAsync("big")).ConverterVersion.ShouldBe(ImportOptions.ConverterVersion);
    }

    [Fact]
    public async Task A_note_that_is_content_and_marked_by_mistake_is_converted_again_unchanged()
    {
        await StartAsync();
        var jex = Library("Just a [file](:/readme)").Resource("readme", "readme.md", "# Read me"u8.ToArray(), "text/markdown");
        await ImportAsync(jex);
        var page = await PageOfAsync("big");
        await AsBeforeAsync(page.Id);

        await ImportAsync(jex);

        (await FileTextAsync(page.Id)).ShouldBe("");
        (await FindAsync("just")).ShouldBe([page.Id]);
    }
}
