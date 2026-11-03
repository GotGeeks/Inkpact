using FsCheck.Xunit;
using Inkpact.Api.Import;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;
namespace Inkpact.Api.Tests.Import;

/// <summary>The id a page gets from the note it was made from (run 1.16c): the same every time, and different for every note and workspace.</summary>
public sealed class PageIdsTests
{
    private static readonly Guid Workspace = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string Note = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_same_note_in_the_same_workspace_always_gets_the_same_id()
    {
        PageIds.For(Workspace, Note).ShouldBe(PageIds.For(Workspace, Note));
    }

    [Fact]
    public void Another_note_or_another_workspace_gets_another_id()
    {
        var id = PageIds.For(Workspace, Note);

        PageIds.For(Workspace, "fedcba9876543210fedcba9876543210").ShouldNotBe(id);
        PageIds.For(Guid.Parse("11111111-2222-3333-4444-555555555556"), Note).ShouldNotBe(id);
    }

    [Fact]
    public void The_id_is_a_version_8_UUID_in_the_form_the_client_accepts()
    {
        var text = PageIds.For(Workspace, Note).ToString("D");

        text.ShouldMatch("^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");
    }

    [Property(MaxTest = 500)]
    public bool Ids_of_different_notes_differ_and_always_have_the_version_and_variant_bits(Guid workspace, Guid first, Guid second)
    {
        var a = PageIds.For(workspace, first.ToString("N"));
        var b = PageIds.For(workspace, second.ToString("N"));
        return a.ToString("D")[14] == '8' && "89ab".Contains(a.ToString("D")[19]) && (first == second ? a == b : a != b);
    }
}

/// <summary>Links between notes through the whole import (run 1.16c): every page has the id its Joplin note gives it, whether or not the note it links to is there yet.</summary>
[Collection(PostgresCollection.Name)]
public sealed class NoteLinkTests(PostgresFixture postgres) : ReimportHarness(postgres)
{
    private static string Link(string text, string note) => $"[{text}](:/{JexBuilder.Id(note)})";

    [Fact]
    public async Task Notebooks_and_notes_get_the_id_their_Joplin_id_gives_them()
    {
        await StartAsync();

        await ImportAsync(new JexBuilder().Folder("work").Note("plan", "work", body: "text"));

        (await PageOfAsync("work")).Id.ShouldBe(PageIds.For(WorkspaceId, JexBuilder.Id("work")));
        (await PageOfAsync("plan")).Id.ShouldBe(PageIds.For(WorkspaceId, JexBuilder.Id("plan")));
    }

    [Fact]
    public async Task A_link_to_a_note_in_the_export_needs_no_line_in_the_report_whichever_comes_first()
    {
        await StartAsync();
        var jex = new JexBuilder()
            .Folder("work")
            .Note("a", "work", body: $"To {Link("b", "b")} and its notebook {Link("work", "work")}.")
            .Note("b", "work", body: $"Back to {Link("a", "a")}.");

        var import = await ImportAsync(jex);

        CountsOf(import).Failed.ShouldBe(0);
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_link_to_a_note_that_is_not_there_is_reported_and_points_to_the_page_it_will_have()
    {
        await StartAsync();
        JexBuilder Export(bool withTarget)
        {
            var jex = new JexBuilder().Folder("work").Note("a", "work", body: $"See {Link("later", "b")}.");
            return withTarget ? jex.Note("b", "work", body: "The target") : jex;
        }

        var first = await ImportAsync(Export(withTarget: false));
        EntryOf(first, "a").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.NoteLinkMissing]);

        var second = await ImportAsync(Export(withTarget: true));

        // The note arrives with the id the link was written with.
        (await PageOfAsync("b")).Id.ShouldBe(PageIds.For(WorkspaceId, JexBuilder.Id("b")));
        CountsOf(second).Failed.ShouldBe(0);
    }

    [Fact]
    public async Task A_link_to_a_note_this_import_skips_is_reported_as_one_it_does_not_make()
    {
        await StartAsync();
        var jex = new JexBuilder()
            .Folder("work")
            .Note("a", "work", body: $"See {Link("binned", "binned")} and {Link("secret", "secret")}.")
            .Note("binned", "work", body: "x", extra: new() { ["deleted_time"] = "1793358000000" })
            .Note("secret", "work", body: "x", extra: new() { ["encryption_applied"] = "1" });

        var import = await ImportAsync(jex);

        EntryOf(import, "a").Reasons.Select(r => (r.Code, r.Count)).ShouldBe([(NoteCodes.NoteLinkMissing, 2)]);
    }

    [Fact]
    public async Task A_link_to_a_tag_is_no_page_link_and_keeps_its_text()
    {
        await StartAsync();
        var jex = new JexBuilder().Folder("work").Tag("urgent").Note("a", "work", body: $"See {Link("the tag", "urgent")}.");

        var import = await ImportAsync(jex);

        EntryOf(import, "a").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ResourceMissing]);
    }

    [Fact]
    public async Task A_note_whose_id_another_page_has_taken_is_imported_with_an_id_of_its_own()
    {
        await StartAsync();
        var taken = PageIds.For(WorkspaceId, JexBuilder.Id("b"));
        await UseDbAsync(async db =>
        {
            db.Pages.Add(new Inkpact.Data.Entities.Page { Id = taken, WorkspaceId = WorkspaceId, Title = "Got there first", SortKey = "a0", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        var import = await ImportAsync(new JexBuilder().Folder("work").Note("b", "work", body: "The note"));

        CountsOf(import).Failed.ShouldBe(0);
        var page = await PageOfAsync("b");
        page.Id.ShouldNotBe(taken);
        page.Title.ShouldBe("b");
    }

    [Fact]
    public async Task A_page_that_version_1_made_is_converted_again_and_one_changed_here_is_not()
    {
        await StartAsync();
        JexBuilder Export() => new JexBuilder().Folder("work")
            .Note("a", "work", body: $"See {Link("b", "b")}.")
            .Note("mine", "work", body: $"See {Link("b", "b")}.")
            .Note("b", "work", body: "The target");
        await ImportAsync(Export());
        await PersonEditsAsync((await PageOfAsync("mine")).Id, "Written here");
        await UseDbAsync(db => db.ImportItems.ExecuteUpdateAsync(set => set.SetProperty(i => i.ConverterVersion, 1)));

        var import = await ImportAsync(Export());

        var counts = CountsOf(import);
        (counts.Reconverted, counts.KeptInInkpact, counts.Failed).ShouldBe((2, 1, 0));
        (await ItemOfAsync("a")).ConverterVersion.ShouldBe(ImportOptions.ConverterVersion);
    }
}
