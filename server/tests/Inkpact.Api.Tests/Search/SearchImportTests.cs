using System.Text;
using Inkpact.Api.Compaction;
using Inkpact.Api.Search;
using Inkpact.Api.Tests.Import;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Search;

/// <summary>What the import leaves behind for search: the text, and the names of the tags, kept in step by every import path.</summary>
public sealed class SearchImportTests(PostgresFixture postgres) : ReimportHarness(postgres)
{
    private static JexBuilder Library(string body = "The quarterly report is due on Friday.") =>
        new JexBuilder().Folder("work", title: "Projects").Note("plan", "work", body: body, title: "Plan");

    private async Task<Guid[]> FindAsync(string text) =>
        [.. (await UseDbAsync(db => new SearchService(db).SearchAsync(WorkspaceId, text))).Results.Select(r => r.Id)];

    private async Task<string> TagTextAsync(string name) =>
        await UseDbAsync(db => db.Pages.AsNoTracking().Where(p => p.ExternalId == JexBuilder.Id(name)).Select(p => p.TagText).SingleAsync());

    [Fact]
    public async Task An_imported_note_is_found_by_a_word_in_its_text_and_by_its_notebook_title()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = (await PageOfAsync("plan")).Id;

        (await FindAsync("quart")).ShouldBe([plan]);
        (await FindAsync("projects")).ShouldBe([(await PageOfAsync("work")).Id]);
    }

    [Fact]
    public async Task An_imported_note_is_found_by_its_tags()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-tax", "Taxes").Tag("t-home", "Home").NoteTag("plan", "t-tax").NoteTag("plan", "t-home"));
        var plan = (await PageOfAsync("plan")).Id;

        (await FindAsync("taxes")).ShouldBe([plan]);
        (await FindAsync("hom")).ShouldBe([plan]);
        (await TagTextAsync("plan")).ShouldBe("Home Taxes");
        (await TagTextAsync("work")).ShouldBe("");
    }

    [Fact]
    public async Task A_tag_that_Joplin_renamed_finds_its_pages_by_the_new_name_and_not_the_old_one()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-tax", "Taxes").NoteTag("plan", "t-tax"));
        var plan = (await PageOfAsync("plan")).Id;

        await ImportAsync(Library().Tag("t-tax", "Fiscal").NoteTag("plan", "t-tax"));

        (await FindAsync("fiscal")).ShouldBe([plan]);
        (await FindAsync("taxes")).ShouldBeEmpty();
        (await TagTextAsync("plan")).ShouldBe("Fiscal");
    }

    [Fact]
    public async Task A_tag_that_Joplin_added_to_a_note_and_one_it_removed_follow_a_second_import()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-tax", "Taxes").Tag("t-home", "Home").NoteTag("plan", "t-tax"));
        var plan = (await PageOfAsync("plan")).Id;

        await ImportAsync(Library().Tag("t-tax", "Taxes").Tag("t-home", "Home").NoteTag("plan", "t-home"));

        (await FindAsync("home")).ShouldBe([plan]);
        (await FindAsync("taxes")).ShouldBeEmpty();
        (await TagTextAsync("plan")).ShouldBe("Home");

        await ImportAsync(Library().Tag("t-tax", "Taxes").Tag("t-home", "Home"));

        (await FindAsync("home")).ShouldBeEmpty();
        (await TagTextAsync("plan")).ShouldBe("");
    }

    [Fact]
    public async Task A_note_changed_in_Joplin_is_found_by_its_new_text_at_once()
    {
        await StartAsync();
        await ImportAsync(Library());
        var plan = (await PageOfAsync("plan")).Id;

        await ImportAsync(Library("Rewritten about the annual budget."));

        (await FindAsync("budget")).ShouldBe([plan]);
        (await FindAsync("quarterly")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_note_of_seven_and_a_half_megabytes_imports_and_compacts_and_is_found_by_a_word_at_its_start()
    {
        await StartAsync();
        var paragraph = string.Concat(Enumerable.Repeat("ordinary words for a very long note ", 14));
        var body = new StringBuilder("startingword at the very top of the note\n\n");
        for (var i = 0; body.Length < 7_500_000; i++)
        {
            body.Append(paragraph).Append(i).Append("\n\n");
        }

        body.Append("endingword at the very bottom of the note");
        var import = await ImportAsync(Library(body.ToString()));

        CountsOf(import).Failed.ShouldBe(0, import.Report);
        EntriesOf(import).ShouldBeEmpty("a note under the limits is content, not a file");
        var plan = (await PageOfAsync("plan")).Id;
        (await FindAsync("startingword")).ShouldBe([plan]);

        (await Service<Compactor>().CompactAsync(plan, default)).ShouldBe(CompactionResult.Compacted);

        (await FindAsync("startingword")).ShouldBe([plan]);
        (await FindAsync("endingword")).ShouldBeEmpty();
        (await UseDbAsync(db => db.Pages.Where(p => p.Id == plan).Select(p => p.ContentText.Length).SingleAsync())).ShouldBeGreaterThan(7_000_000);
    }
}
