using Inkpact.Api.Import;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// A note or notebook that Joplin has put in its trash since the last import (run 1.16b): its page follows into the trash
/// here only when nothing was changed here, and the person can restore it from there.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReimportTrashTests(PostgresFixture postgres) : ReimportHarness(postgres)
{
    private const string Deleted = "1795000000000";

    private static Dictionary<string, string> InTrash => new() { ["deleted_time"] = Deleted };

    private static JexBuilder Library(bool planDeleted = false, bool otherDeleted = false, bool workDeleted = false) => new JexBuilder()
        .Folder("work", extra: workDeleted ? InTrash : null)
        .Note("plan", "work", body: "Original text", title: "Aplan", extra: planDeleted ? InTrash : null)
        .Note("other", "work", body: "Another note", title: "Bother", extra: otherDeleted ? InTrash : null);

    [Fact]
    public async Task A_note_that_is_in_Joplins_trash_and_untouched_here_goes_to_the_trash_and_stays_there()
    {
        await StartAsync();
        await ImportAsync(Library());

        var import = await ImportAsync(Library(planDeleted: true));

        var counts = CountsOf(import);
        (counts.MovedToTrash, counts.Trashed, counts.KeptInInkpact, counts.AlreadyImported).ShouldBe((1, 0, 0, 2));
        var plan = await PageOfAsync("plan");
        plan.DeletedAt.ShouldNotBeNull();
        (await PageOfAsync("other")).DeletedAt.ShouldBeNull();
        EntriesOf(import).ShouldBeEmpty();

        // Restored by hand, it is the person's again: a note that is in Joplin's trash is counted, not looked at.
        var again = await ImportAsync(Library(planDeleted: true));
        (CountsOf(again).MovedToTrash, CountsOf(again).Trashed).ShouldBe((0, 1));
        (await PageOfAsync("plan")).DeletedAt.ShouldBe(plan.DeletedAt);
    }

    [Fact]
    public async Task A_note_whose_title_was_changed_here_stays_and_the_report_says_so()
    {
        await StartAsync();
        await ImportAsync(Library());
        await EditPageAsync("plan", p => p.Title = "Mine");

        var import = await ImportAsync(Library(planDeleted: true));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        var counts = CountsOf(import);
        (counts.MovedToTrash, counts.Trashed, counts.KeptInInkpact).ShouldBe((0, 0, 1));
        var entry = EntryOf(import, "plan");
        entry.Status.ShouldBe(ReportStatus.Kept);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.DeletedInJoplinKept]);
        entry.PageId.ShouldBe((await PageOfAsync("plan")).Id);
    }

    [Fact]
    public async Task A_note_whose_content_was_changed_here_stays()
    {
        await StartAsync();
        await ImportAsync(Library());
        await PersonEditsAsync((await PageOfAsync("plan")).Id, "I wrote this");

        var import = await ImportAsync(Library(planDeleted: true));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        CountsOf(import).KeptInInkpact.ShouldBe(1);
    }

    [Fact]
    public async Task A_note_with_a_tag_added_here_or_moved_here_stays()
    {
        await StartAsync();
        await ImportAsync(Library().Folder("elsewhere"));
        var plan = await PageOfAsync("plan");
        var elsewhere = await PageOfAsync("elsewhere");
        await UseDbAsync(async db =>
        {
            var tag = new Tag { WorkspaceId = WorkspaceId, Name = "Mine", NormalizedName = "mine", CreatedAt = DateTimeOffset.UtcNow };
            db.Tags.Add(tag);
            db.PageTags.Add(new PageTag { PageId = plan.Id, TagId = tag.Id });
            await db.SaveChangesAsync();
            return 0;
        });
        await EditPageAsync("other", p => p.ParentId = elsewhere.Id);

        await ImportAsync(Library(planDeleted: true, otherDeleted: true).Folder("elsewhere"));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("other")).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_note_made_by_run_1_15_without_a_base_stays()
    {
        await StartAsync();
        await ImportAsync(Library());
        await UseDbAsync(async db =>
        {
            await db.ImportItems.ExecuteUpdateAsync(set => set.SetProperty(i => i.Base, (string?)null));
            return 0;
        });

        var import = await ImportAsync(Library(planDeleted: true));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        CountsOf(import).KeptInInkpact.ShouldBe(1);
    }

    [Fact]
    public async Task A_note_missing_from_the_export_is_not_deleted()
    {
        await StartAsync();
        await ImportAsync(Library());

        var import = await ImportAsync(new JexBuilder().Folder("work").Note("other", "work", body: "Another note", title: "Bother"));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        CountsOf(import).MovedToTrash.ShouldBe(0);
    }

    // ---- notebooks -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_notebook_and_all_its_notes_in_Joplins_trash_go_as_one_so_restoring_the_notebook_restores_them_all()
    {
        await StartAsync();
        await ImportAsync(Library());

        var import = await ImportAsync(Library(planDeleted: true, otherDeleted: true, workDeleted: true));

        CountsOf(import).MovedToTrash.ShouldBe(3);
        (await PageOfAsync("work")).DeletedAt.ShouldNotBeNull();
        // Only the highest page is in the trash; the notes are in it because their notebook is.
        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("other")).DeletedAt.ShouldBeNull();
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notebook_with_a_page_made_here_stays_while_its_notes_go()
    {
        await StartAsync();
        await ImportAsync(Library());
        var work = await PageOfAsync("work");
        var mine = new Page { WorkspaceId = WorkspaceId, ParentId = work.Id, Title = "Made here", SortKey = "zz", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await UseDbAsync(async db =>
        {
            db.Pages.Add(mine);
            await db.SaveChangesAsync();
            return 0;
        });

        var import = await ImportAsync(Library(planDeleted: true, otherDeleted: true, workDeleted: true));

        (await PageOfAsync("work")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("plan")).DeletedAt.ShouldNotBeNull();
        (await PageOfAsync("other")).DeletedAt.ShouldNotBeNull();
        var counts = CountsOf(import);
        (counts.MovedToTrash, counts.KeptInInkpact).ShouldBe((2, 1));
        // Not changed here itself: it stays for what is in it.
        EntryOf(import, "work").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.DeletedInJoplinNotEmpty]);
        (await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Id == mine.Id))).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_notebook_with_a_note_that_Joplin_kept_stays_and_so_does_that_note()
    {
        await StartAsync();
        await ImportAsync(Library());

        var import = await ImportAsync(Library(planDeleted: true, workDeleted: true));

        (await PageOfAsync("work")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("plan")).DeletedAt.ShouldNotBeNull();
        (await PageOfAsync("other")).DeletedAt.ShouldBeNull();
        var counts = CountsOf(import);
        (counts.MovedToTrash, counts.KeptInInkpact).ShouldBe((1, 1));
        EntryOf(import, "work").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.DeletedInJoplinNotEmpty]);
    }

    [Fact]
    public async Task A_note_moved_to_the_top_here_out_of_a_notebook_put_in_the_trash_here_stays()
    {
        await StartAsync();
        await ImportAsync(Library());
        (await PersonMovesAsync("plan", null)).ShouldBeNull();
        await EditPageAsync("work", p => p.DeletedAt = DateTimeOffset.UtcNow);

        var import = await ImportAsync(Library(planDeleted: true));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.DeletedInJoplinKept]);
    }

    [Fact]
    public async Task A_page_that_cannot_be_looked_at_stays_is_counted_as_failed_and_keeps_its_notebook()
    {
        await StartAsync();
        await ImportAsync(Library());
        // A base that does not read: the look at the page fails, as it would with the sidecar down.
        await UseDbAsync(async db =>
        {
            var plan = JexBuilder.Id("plan");
            await db.ImportItems.Where(i => i.ExternalId == plan).ExecuteUpdateAsync(set => set.SetProperty(i => i.Base, "[]"));
            return 0;
        });

        var import = await ImportAsync(Library(planDeleted: true, otherDeleted: true, workDeleted: true));

        (await PageOfAsync("plan")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("work")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("other")).DeletedAt.ShouldNotBeNull();
        var counts = CountsOf(import);
        (counts.Failed, counts.MovedToTrash, counts.KeptInInkpact).ShouldBe((1, 1, 1));
        var entry = EntryOf(import, "plan");
        entry.Status.ShouldBe(ReportStatus.Failed);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.ItemFailed]);
        EntryOf(import, "work").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.DeletedInJoplinNotEmpty]);
    }

    [Fact]
    public async Task A_deep_chain_of_notebooks_in_Joplins_trash_goes_as_one()
    {
        const int Depth = 2000;
        static JexBuilder Chain(bool deleted)
        {
            var jex = new JexBuilder();
            for (var i = 0; i < Depth; i++)
            {
                jex.Folder($"n{i}", parent: i == 0 ? null : $"n{i - 1}", extra: deleted ? InTrash : null);
            }

            return jex;
        }

        await StartAsync();
        await ImportAsync(Chain(deleted: false));

        var import = await ImportAsync(Chain(deleted: true));

        CountsOf(import).MovedToTrash.ShouldBe(Depth);
        (await PageOfAsync("n0")).DeletedAt.ShouldNotBeNull();
        (await PageOfAsync($"n{Depth - 1}")).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_notebook_that_was_renamed_here_stays_with_its_notes_gone()
    {
        await StartAsync();
        await ImportAsync(Library());
        await EditPageAsync("work", p => p.Title = "Renamed here");

        await ImportAsync(Library(planDeleted: true, otherDeleted: true, workDeleted: true));

        (await PageOfAsync("work")).DeletedAt.ShouldBeNull();
        (await PageOfAsync("plan")).DeletedAt.ShouldNotBeNull();
    }
}
