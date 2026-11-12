using Inkpact.Api.Import;
using Inkpact.Api.Tags;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Import;

/// <summary>
/// Importing an export again (run 1.16b): a note's title, place, to-do fields and tags, and a notebook's and a tag's name,
/// are compared three ways. What Joplin changed alone is taken, and what was changed here stays. The real database, hub and
/// Yjs sidecar.
/// </summary>
public sealed class ReimportFieldsTests(PostgresFixture postgres) : ReimportHarness(postgres)
{
    private static JexBuilder Library(string title = "Plan", string body = "Original text", string parent = "work", bool todo = false, string? due = null, string? completed = null) =>
        new JexBuilder()
            .Folder("work")
            .Folder("other")
            .Note("plan", parent, body: body, title: title, todo: todo, due: due, completed: completed);

    // ---- title -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_title_changed_only_in_Joplin_is_taken_and_the_same_export_again_changes_nothing()
    {
        await StartAsync();
        await ImportAsync(Library());
        var before = await PageOfAsync("plan");

        var import = await ImportAsync(Library("Plan, second version"));

        var page = await PageOfAsync("plan");
        page.Title.ShouldBe("Plan, second version");
        page.Version.ShouldBe(before.Version + 1);
        page.ContentText.ShouldBe(before.ContentText);
        var counts = CountsOf(import);
        (counts.Updated, counts.AlreadyImported, counts.KeptInInkpact, counts.Failed).ShouldBe((1, 2, 0, 0));
        EntriesOf(import).ShouldBeEmpty();
        ImportBase.FromJson((await ItemOfAsync("plan")).Base)!.Title.ShouldBe("Plan, second version");

        // As after a person's rename, the title waits for the history's capture: the body did not change, so nothing
        // else would ever make a version with it (run B.5b).
        var waiting = (await UseDbAsync(db => db.PageTitleChanges.AsNoTracking().Where(c => c.PageId == page.Id).ToListAsync())).ShouldHaveSingleItem();
        (waiting.Title, waiting.PageVersion, waiting.ChangedBy).ShouldBe(("Plan, second version", page.Version, UserId));

        var again = await ImportAsync(Library("Plan, second version"));
        (CountsOf(again).AlreadyImported, CountsOf(again).Updated).ShouldBe((3, 0));
        (await PageOfAsync("plan")).Version.ShouldBe(page.Version);
        (await UseDbAsync(db => db.PageTitleChanges.CountAsync(c => c.PageId == page.Id))).ShouldBe(1);
    }

    [Fact]
    public async Task A_title_changed_in_both_places_stays_as_it_is_here_and_the_report_says_so_once()
    {
        await StartAsync();
        await ImportAsync(Library());
        await EditPageAsync("plan", p => p.Title = "My own title");

        var import = await ImportAsync(Library("Joplin's new title"));

        (await PageOfAsync("plan")).Title.ShouldBe("My own title");
        CountsOf(import).KeptInInkpact.ShouldBe(1);
        var entry = EntryOf(import, "plan");
        entry.Status.ShouldBe(ReportStatus.Kept);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TitleKept]);

        // Joplin's title has been seen: the same export is not told again.
        var again = await ImportAsync(Library("Joplin's new title"));
        EntriesOf(again).ShouldBeEmpty();
        (await PageOfAsync("plan")).Title.ShouldBe("My own title");
    }

    [Fact]
    public async Task A_title_changed_only_here_stays_when_Joplin_changes_something_else_and_nothing_is_said()
    {
        await StartAsync();
        await ImportAsync(Library());
        await EditPageAsync("plan", p => p.Title = "My own title");

        var import = await ImportAsync(Library(body: "Joplin changed the text"));

        var page = await PageOfAsync("plan");
        page.Title.ShouldBe("My own title");
        (await TextOfAsync(page.Id)).ShouldBe("Joplin changed the text");
        EntriesOf(import).ShouldBeEmpty();
        CountsOf(import).Updated.ShouldBe(1);
    }

    [Fact]
    public async Task A_title_and_a_body_that_both_changed_in_Joplin_are_both_taken_and_the_base_has_both()
    {
        await StartAsync();
        await ImportAsync(Library());

        var import = await ImportAsync(Library("New title", "New body"));

        var page = await PageOfAsync("plan");
        page.Title.ShouldBe("New title");
        (await TextOfAsync(page.Id)).ShouldBe("New body");
        CountsOf(import).Updated.ShouldBe(1);
        var item = await ItemOfAsync("plan");
        var itemBase = ImportBase.FromJson(item.Base)!;
        itemBase.Title.ShouldBe("New title");
        itemBase.ContentKey.ShouldNotBeNull();

        (CountsOf(await ImportAsync(Library("New title", "New body"))).AlreadyImported).ShouldBe(3);
    }

    [Fact]
    public async Task A_page_whose_title_and_body_were_taken_is_updated_at_the_time_of_the_import_not_at_Joplins_older_time()
    {
        await StartAsync();
        await ImportAsync(Library());
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);

        await ImportAsync(Library("New title", "New body"));

        // Joplin's updated time in the export is early in 2026: the content step may not set the page back to it.
        (await PageOfAsync("plan")).UpdatedAt.ShouldBeGreaterThan(started);
    }

    [Fact]
    public async Task A_title_taken_over_is_cleaned_as_on_import_and_the_cleaning_is_in_the_report()
    {
        await StartAsync();
        await ImportAsync(Library());

        var import = await ImportAsync(Library(new string('x', TreeServiceTitle + 20)));

        (await PageOfAsync("plan")).Title.Length.ShouldBe(TreeServiceTitle);
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.Truncated]);
        EntryOf(import, "plan").Status.ShouldBe(ReportStatus.Updated);
    }

    private const int TreeServiceTitle = Inkpact.Api.Pages.TreeService.MaxTitleLength;

    // ---- place -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_note_moved_in_Joplin_moves_to_the_end_of_the_new_notebook()
    {
        await StartAsync();
        await ImportAsync(Library().Note("sibling", "other", body: "x"));
        var before = await PageOfAsync("plan");
        var treeVersion = await UseDbAsync(db => db.Workspaces.Select(w => w.TreeVersion).SingleAsync());

        var import = await ImportAsync(Library(parent: "other").Note("sibling", "other", body: "x"));

        var page = await PageOfAsync("plan");
        var other = await PageOfAsync("other");
        var sibling = await PageOfAsync("sibling");
        page.ParentId.ShouldBe(other.Id);
        string.CompareOrdinal(page.SortKey, sibling.SortKey).ShouldBeGreaterThan(0);
        page.Version.ShouldBe(before.Version + 1);
        (await UseDbAsync(db => db.Workspaces.Select(w => w.TreeVersion).SingleAsync())).ShouldBeGreaterThan(treeVersion);
        CountsOf(import).Updated.ShouldBe(1);
        EntriesOf(import).ShouldBeEmpty();
        ImportBase.FromJson((await ItemOfAsync("plan")).Base)!.ParentId.ShouldBe(JexBuilder.Id("other"));
    }

    [Fact]
    public async Task A_note_that_was_moved_here_too_stays_where_it_is_here()
    {
        await StartAsync();
        await ImportAsync(Library().Folder("third"));
        var third = await PageOfAsync("third");
        await EditPageAsync("plan", p => p.ParentId = third.Id);

        var import = await ImportAsync(Library(parent: "other").Folder("third"));

        (await PageOfAsync("plan")).ParentId.ShouldBe(third.Id);
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.NotebookKept]);
        EntryOf(import, "plan").Status.ShouldBe(ReportStatus.Kept);
    }

    [Fact]
    public async Task A_note_moved_only_here_stays_where_it_is_here_when_Joplin_changes_something_else()
    {
        await StartAsync();
        await ImportAsync(Library());
        var other = await PageOfAsync("other");
        await EditPageAsync("plan", p => p.ParentId = other.Id);

        var import = await ImportAsync(Library("A new title"));

        var page = await PageOfAsync("plan");
        page.ParentId.ShouldBe(other.Id);
        page.Title.ShouldBe("A new title");
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_move_into_a_notebook_in_the_trash_is_refused_and_tried_again_when_the_notebook_is_back()
    {
        await StartAsync();
        await ImportAsync(Library());
        var workPage = await PageOfAsync("work");
        var otherPage = await PageOfAsync("other");
        var hashBefore = (await ItemOfAsync("plan")).ContentHash;
        await EditPageAsync("other", p => p.DeletedAt = DateTimeOffset.UtcNow);

        var refused = await ImportAsync(Library(parent: "other"));

        (await PageOfAsync("plan")).ParentId.ShouldBe(workPage.Id);
        EntryOf(refused, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.MoveTargetInTrash]);
        var item = await ItemOfAsync("plan");
        item.ContentHash.ShouldBe(hashBefore);
        ImportBase.FromJson(item.Base)!.ParentId.ShouldBe(JexBuilder.Id("work"));

        await EditPageAsync("other", p => p.DeletedAt = null);
        var moved = await ImportAsync(Library(parent: "other"));

        (await PageOfAsync("plan")).ParentId.ShouldBe(otherPage.Id);
        CountsOf(moved).Updated.ShouldBe(1);
        EntriesOf(moved).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_note_moved_to_the_top_here_out_of_a_notebook_put_in_the_trash_here_stays_at_the_top()
    {
        await StartAsync();
        await ImportAsync(Library());
        // The old notebook is in the trash here, so "where it was" is no page at all, and not the top level.
        (await PersonMovesAsync("plan", null)).ShouldBeNull();
        await EditPageAsync("work", p => p.DeletedAt = DateTimeOffset.UtcNow);

        var import = await ImportAsync(Library(parent: "other"));

        (await PageOfAsync("plan")).ParentId.ShouldBeNull();
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.NotebookKept]);
    }

    [Fact]
    public async Task A_move_to_a_notebook_that_is_not_imported_is_refused_and_said()
    {
        await StartAsync();
        await ImportAsync(Library());
        var workPage = await PageOfAsync("work");

        var import = await ImportAsync(Library(parent: "missing"));

        (await PageOfAsync("plan")).ParentId.ShouldBe(workPage.Id);
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.MoveTargetMissing]);
    }

    [Fact]
    public async Task Two_notebooks_moved_into_each_other_one_in_each_app_make_no_cycle()
    {
        await StartAsync();
        var jex = () => new JexBuilder().Folder("a").Folder("b");
        await ImportAsync(jex());
        var a = await PageOfAsync("a");
        // Here b went under a; in Joplin a goes under b.
        await EditPageAsync("b", p => p.ParentId = a.Id);

        var import = await ImportAsync(new JexBuilder().Folder("b").Folder("a", parent: "b"));

        (await PageOfAsync("a")).ParentId.ShouldBeNull();
        (await PageOfAsync("b")).ParentId.ShouldBe(a.Id);
        EntryOf(import, "a").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.MoveCycle]);
    }

    [Fact]
    public async Task A_notebook_renamed_and_moved_in_Joplin_follows_the_same_rule()
    {
        await StartAsync();
        await ImportAsync(new JexBuilder().Folder("a").Folder("b").Folder("c", title: "Old"));

        var import = await ImportAsync(new JexBuilder().Folder("a").Folder("b").Folder("c", parent: "b", title: "New"));

        var page = await PageOfAsync("c");
        page.Title.ShouldBe("New");
        page.ParentId.ShouldBe((await PageOfAsync("b")).Id);
        CountsOf(import).Updated.ShouldBe(1);
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notebook_renamed_here_keeps_its_name_when_Joplin_renames_it_too()
    {
        await StartAsync();
        await ImportAsync(new JexBuilder().Folder("a", title: "Old"));
        await EditPageAsync("a", p => p.Title = "Mine");

        var import = await ImportAsync(new JexBuilder().Folder("a", title: "Joplin's"));

        (await PageOfAsync("a")).Title.ShouldBe("Mine");
        EntryOf(import, "a").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TitleKept]);
    }

    // ---- to-do fields ----------------------------------------------------------------------------------------------------

    private Task<List<Reminder>> RemindersAsync(Guid pageId) =>
        UseDbAsync(db => db.Reminders.AsNoTracking().Where(r => r.PageId == pageId).ToListAsync());

    private static DateTimeOffset At(string milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(milliseconds));

    [Fact]
    public async Task A_due_date_and_completion_changed_in_Joplin_are_taken_and_the_reminder_follows_the_date()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true, due: DueOne));
        var page = await PageOfAsync("plan");
        (await RemindersAsync(page.Id)).Select(r => r.RemindAt).ShouldBe([At(DueOne)]);

        var import = await ImportAsync(Library(todo: true, due: DueTwo, completed: DoneOne));

        page = await PageOfAsync("plan");
        page.DueAt.ShouldBe(At(DueTwo));
        page.CompletedAt.ShouldBe(At(DoneOne));
        (await RemindersAsync(page.Id)).Select(r => r.RemindAt).ShouldBe([At(DueTwo)]);
        CountsOf(import).Updated.ShouldBe(1);
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_due_date_that_is_taken_away_in_Joplin_removes_the_reminder_and_one_that_is_added_makes_one()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true, due: DueOne));
        var page = await PageOfAsync("plan");

        await ImportAsync(Library(todo: true));
        (await PageOfAsync("plan")).DueAt.ShouldBeNull();
        (await RemindersAsync(page.Id)).ShouldBeEmpty();

        await ImportAsync(Library(todo: true, due: DueTwo));
        (await PageOfAsync("plan")).DueAt.ShouldBe(At(DueTwo));
        (await RemindersAsync(page.Id)).Select(r => r.RemindAt).ShouldBe([At(DueTwo)]);
    }

    [Fact]
    public async Task A_reminder_that_was_delivered_stays_and_the_new_date_gets_a_reminder_of_its_own()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true, due: DueOne));
        var page = await PageOfAsync("plan");
        await UseDbAsync(async db =>
        {
            await db.Reminders.ExecuteUpdateAsync(set => set.SetProperty(r => r.DeliveredAt, DateTimeOffset.UtcNow));
            return 0;
        });

        await ImportAsync(Library(todo: true, due: DueTwo));

        var reminders = await RemindersAsync(page.Id);
        reminders.Count.ShouldBe(2);
        reminders.Single(r => r.DeliveredAt is not null).RemindAt.ShouldBe(At(DueOne));
        reminders.Single(r => r.DeliveredAt is null).RemindAt.ShouldBe(At(DueTwo));
    }

    [Fact]
    public async Task A_note_that_stops_being_a_to_do_in_Joplin_stops_being_one_here()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true, due: DueOne, completed: DoneOne));

        await ImportAsync(Library());

        var page = await PageOfAsync("plan");
        (page.IsTodo, page.DueAt, page.CompletedAt).ShouldBe((false, null, null));
        (await RemindersAsync(page.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_to_do_field_follows_the_rule_on_its_own()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true, due: DueOne));
        // Completed here; Joplin completes it at another time and moves the due date.
        await EditPageAsync("plan", p => p.CompletedAt = At(DoneOne));

        var import = await ImportAsync(Library(todo: true, due: DueTwo, completed: DoneTwo));

        var page = await PageOfAsync("plan");
        page.CompletedAt.ShouldBe(At(DoneOne));
        page.DueAt.ShouldBe(At(DueTwo));
        var entry = EntryOf(import, "plan");
        entry.Status.ShouldBe(ReportStatus.Updated);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.CompletedKept]);
    }

    [Fact]
    public async Task A_to_do_whose_due_date_changed_here_stays_a_to_do_when_Joplin_makes_it_a_note()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true, due: DueOne));
        await EditPageAsync("plan", p => p.DueAt = At(DueTwo));

        var import = await ImportAsync(Library());

        // Not a note with a due date: the to-do is one thing, changed in both places.
        var page = await PageOfAsync("plan");
        (page.IsTodo, page.DueAt, page.CompletedAt).ShouldBe((true, At(DueTwo), null));
        var entry = EntryOf(import, "plan");
        entry.Status.ShouldBe(ReportStatus.Kept);
        entry.Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TodoKept]);
    }

    [Fact]
    public async Task A_to_do_made_a_note_here_is_not_completed_when_Joplin_completes_it()
    {
        await StartAsync();
        await ImportAsync(Library(todo: true));
        await EditPageAsync("plan", p => p.IsTodo = false);

        var import = await ImportAsync(Library(todo: true, completed: DoneOne));

        var page = await PageOfAsync("plan");
        (page.IsTodo, page.DueAt, page.CompletedAt).ShouldBe((false, null, null));
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TodoKept]);
    }

    // ---- tags ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tag_added_or_removed_in_Joplin_follows_even_though_the_note_file_did_not_change()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-work", "Work").NoteTag("plan", "t-work"));
        var page = await PageOfAsync("plan");
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Work"]);

        var added = await ImportAsync(Library().Tag("t-work", "Work").Tag("t-home", "Home").NoteTag("plan", "t-work").NoteTag("plan", "t-home"));
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Home", "Work"]);
        CountsOf(added).Updated.ShouldBe(1);

        var removed = await ImportAsync(Library().Tag("t-work", "Work").Tag("t-home", "Home").NoteTag("plan", "t-home"));
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Home"]);
        CountsOf(removed).Updated.ShouldBe(1);

        var again = await ImportAsync(Library().Tag("t-work", "Work").Tag("t-home", "Home").NoteTag("plan", "t-home"));
        (CountsOf(again).Updated, CountsOf(again).AlreadyImported).ShouldBe((0, 5));
    }

    [Fact]
    public async Task A_tag_added_here_is_never_touched_and_one_removed_here_is_not_added_back()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-work", "Work").Tag("t-home", "Home").NoteTag("plan", "t-work"));
        var page = await PageOfAsync("plan");
        var work = await TagOfAsync("t-work");
        var home = await TagOfAsync("t-home");
        await UseDbAsync(async db =>
        {
            var mine = new Tag { WorkspaceId = WorkspaceId, Name = "Mine", NormalizedName = "mine", CreatedAt = DateTimeOffset.UtcNow };
            db.Tags.Add(mine);
            db.PageTags.Add(new PageTag { PageId = page.Id, TagId = mine.Id });
            db.PageTags.RemoveRange(db.PageTags.Where(t => t.PageId == page.Id && t.TagId == work.Id));
            await db.SaveChangesAsync();
            return 0;
        });

        // Joplin changes nothing about this note's tags; its file changes otherwise.
        await ImportAsync(Library("A new title").Tag("t-work", "Work").Tag("t-home", "Home").NoteTag("plan", "t-work"));
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Mine"]);

        // Joplin adds one: it comes, and the rest is as it was here.
        await ImportAsync(Library("A new title").Tag("t-work", "Work").Tag("t-home", "Home").NoteTag("plan", "t-work").NoteTag("plan", "t-home"));
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Home", "Mine"]);
        home.Name.ShouldBe("Home");
    }

    [Fact]
    public async Task Two_tags_of_Joplin_that_are_one_tag_here_are_removed_only_when_both_are()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-a", "Work").Tag("t-b", "work").NoteTag("plan", "t-a").NoteTag("plan", "t-b"));
        var page = await PageOfAsync("plan");
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Work"]);

        await ImportAsync(Library().Tag("t-a", "Work").Tag("t-b", "work").NoteTag("plan", "t-a"));
        (await TagNamesOfAsync(page.Id)).ShouldBe(["Work"]);

        await ImportAsync(Library().Tag("t-a", "Work").Tag("t-b", "work"));
        (await TagNamesOfAsync(page.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tag_renamed_in_Joplin_is_renamed_here_when_it_was_not_renamed_here()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-work", "Work").NoteTag("plan", "t-work"));

        var import = await ImportAsync(Library().Tag("t-work", "Job").NoteTag("plan", "t-work"));

        var tag = await TagOfAsync("t-work");
        (tag.Name, tag.NormalizedName).ShouldBe(("Job", "job"));
        CountsOf(import).Updated.ShouldBe(1);
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tag_renamed_in_Joplin_keeps_its_name_when_it_was_renamed_here_too()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-work", "Work"));
        await UseDbAsync(async db =>
        {
            await db.Tags.ExecuteUpdateAsync(set => set.SetProperty(t => t.Name, "Mine").SetProperty(t => t.NormalizedName, "mine"));
            return 0;
        });

        var import = await ImportAsync(Library().Tag("t-work", "Job"));

        (await TagOfAsync("t-work")).Name.ShouldBe("Mine");
        var entry = EntryOf(import, "t-work");
        entry.Reasons.Select(r => (r.Code, r.Detail)).ShouldBe([(NoteCodes.TagNameKept, "Job")]);
    }

    [Fact]
    public async Task A_tag_removed_with_the_tag_endpoint_stays_removed_after_the_same_export_is_imported_again()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-work", "Work").NoteTag("plan", "t-work"));
        var page = await PageOfAsync("plan");
        var work = await TagOfAsync("t-work");

        (await UseScopeAsync<TagService, TagResult<IReadOnlyList<TagDto>>>(tags => tags.ChangeTagsAsync(page.Id, [], [work.Id]))).Succeeded.ShouldBeTrue();

        var import = await ImportAsync(Library().Tag("t-work", "Work").NoteTag("plan", "t-work"));

        // Joplin still has the tag; nothing here asked for it back, so the removal here stands, silently (unlike a
        // title conflict, a tag that stays removed is not its own report entry).
        (await TagNamesOfAsync(page.Id)).ShouldBeEmpty();
        EntriesOf(import).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tag_renamed_with_the_tag_endpoint_keeps_its_name_after_the_same_export_is_imported_again()
    {
        await StartAsync();
        await ImportAsync(Library().Tag("t-work", "Work"));
        var work = await TagOfAsync("t-work");

        var renamed = await UseScopeAsync<TagService, TagResult<TagDto>>(tags => tags.RenameTagAsync(WorkspaceId, work.Id, "Mine"));
        renamed.Succeeded.ShouldBeTrue();

        var import = await ImportAsync(Library().Tag("t-work", "Job"));

        (await TagOfAsync("t-work")).Name.ShouldBe("Mine");
        var entry = EntryOf(import, "t-work");
        entry.Reasons.Select(r => (r.Code, r.Detail)).ShouldBe([(NoteCodes.TagNameKept, "Job")]);
    }

    [Fact]
    public async Task A_tag_made_by_hand_is_not_renamed_from_Joplin_and_neither_is_a_name_that_is_taken()
    {
        await StartAsync();
        await UseDbAsync(async db =>
        {
            db.Tags.Add(new Tag { WorkspaceId = WorkspaceId, Name = "Work", NormalizedName = "work", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        await ImportAsync(Library().Tag("t-work", "Work").Tag("t-home", "Home"));
        // t-work was matched to the tag made by hand; t-home was made for the import. A third tag has the name Joplin wants.
        await UseDbAsync(async db =>
        {
            db.Tags.Add(new Tag { WorkspaceId = WorkspaceId, Name = "Away", NormalizedName = "away", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });

        var import = await ImportAsync(Library().Tag("t-work", "Job").Tag("t-home", "Away"));

        (await UseDbAsync(db => db.Tags.Select(t => t.Name).OrderBy(n => n).ToListAsync())).ShouldBe(["Away", "Home", "Work"]);
        EntryOf(import, "t-work").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TagNameKept]);
        EntryOf(import, "t-home").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TagNameKept]);
    }

    // ---- an item of run 1.15 ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes the items of this export look like run 1.15 left them: no base and no fingerprint, and a hash of the item's file
    /// alone (run 1.15 knew nothing of a note's tags or files).
    /// </summary>
    private Task MakeItemsLikeRun115Async(JexBuilder jex, params string[] names) => UseDbAsync(async db =>
    {
        foreach (var name in names)
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(jex.ItemBytes(name)));
            await db.ImportItems.Where(i => i.ExternalId == JexBuilder.Id(name)).ExecuteUpdateAsync(set => set
                .SetProperty(i => i.Base, (string?)null)
                .SetProperty(i => i.PageFingerprint, (byte[]?)null)
                .SetProperty(i => i.PageSeq, (long?)null)
                .SetProperty(i => i.ContentHash, hash));
        }

        return 0;
    });

    [Fact]
    public async Task An_item_of_run_1_15_whose_file_did_not_change_gets_its_base_and_loses_nothing()
    {
        await StartAsync();
        var jex = () => Library().Tag("t-work", "Work").NoteTag("plan", "t-work");
        await ImportAsync(jex());
        await MakeItemsLikeRun115Async(jex(), "work", "other", "plan", "t-work");
        await EditPageAsync("plan", p => p.Title = "Mine");

        var import = await ImportAsync(jex());

        (await PageOfAsync("plan")).Title.ShouldBe("Mine");
        CountsOf(import).AlreadyImported.ShouldBe(4);
        EntriesOf(import).ShouldBeEmpty();
        var itemBase = ImportBase.FromJson((await ItemOfAsync("plan")).Base)!;
        itemBase.Title.ShouldBe("Plan");
        itemBase.ContentKey.ShouldNotBeNull();
        (await TagNamesOfAsync((await PageOfAsync("plan")).Id)).ShouldBe(["Work"]);
    }

    [Fact]
    public async Task A_field_that_differs_on_an_item_of_run_1_15_keeps_the_value_here_and_the_next_import_knows_the_base()
    {
        await StartAsync();
        await ImportAsync(Library());
        await MakeItemsLikeRun115Async(Library(), "plan");
        await EditPageAsync("plan", p => p.Title = "Mine");

        var first = await ImportAsync(Library("Joplin's title"));

        (await PageOfAsync("plan")).Title.ShouldBe("Mine");
        EntryOf(first, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.BaseUnknown]);

        // Now the base is Joplin's title: a change in Joplin is told apart from a change here.
        var second = await ImportAsync(Library("Joplin's other title"));
        (await PageOfAsync("plan")).Title.ShouldBe("Mine");
        EntryOf(second, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.TitleKept]);
        EntryOf(second, "plan").Status.ShouldBe(ReportStatus.Kept);
    }

    [Fact]
    public async Task A_note_of_run_1_15_that_changed_in_its_body_gets_the_body_and_keeps_a_title_that_differs()
    {
        await StartAsync();
        await ImportAsync(Library());
        await MakeItemsLikeRun115Async(Library(), "plan");
        await EditPageAsync("plan", p => p.Title = "Mine");

        var import = await ImportAsync(Library(body: "Joplin's new text"));

        var page = await PageOfAsync("plan");
        page.Title.ShouldBe("Mine");
        (await TextOfAsync(page.Id)).ShouldBe("Joplin's new text");
        EntryOf(import, "plan").Reasons.Select(r => r.Code).ShouldBe([NoteCodes.BaseUnknown]);
    }

    // ---- crash -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Fields_written_before_the_content_are_not_undone_and_the_content_still_comes()
    {
        await StartAsync();
        await ImportAsync(Library());
        // The state a crash leaves: the fields step committed (title and base), the content step did not (hash, content key).
        await EditPageAsync("plan", p => p.Title = "New title");
        await UseDbAsync(async db =>
        {
            var item = await db.ImportItems.SingleAsync(i => i.ExternalId == JexBuilder.Id("plan"));
            item.Base = (ImportBase.FromJson(item.Base)! with { Title = "New title" }).ToJson();
            await db.SaveChangesAsync();
            return 0;
        });

        var import = await ImportAsync(Library("New title", "New body"));

        var page = await PageOfAsync("plan");
        page.Title.ShouldBe("New title");
        (await TextOfAsync(page.Id)).ShouldBe("New body");
        EntriesOf(import).ShouldBeEmpty();
        CountsOf(import).Updated.ShouldBe(1);
    }
}
