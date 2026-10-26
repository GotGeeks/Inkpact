using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Inkpact.Api.Tests.Pages;

public class TreeServiceTests(PostgresFixture postgres) : TreeTestBase(postgres)
{
    private static readonly Position AtTheEnd = new(null, null, null);

    // ---- creating ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_page_is_the_first_key_and_the_next_ones_go_last()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        (await OutlineAsync()).ShouldBe(["A", "B", "C"]);
        (await RowAsync(a)).SortKey.ShouldBe("a0");
        (await RowAsync(b)).SortKey.ShouldBe("a1");
        (await RowAsync(c)).SortKey.ShouldBe("a2");
    }

    [Fact]
    public async Task A_new_page_starts_at_version_one_with_the_time_and_no_trash_mark()
    {
        var change = await Succeeded(WithTree(tree => tree.CreateAsync(WorkspaceId, "A", AtTheEnd)));

        change.Page.Version.ShouldBe(1);
        change.Page.UpdatedAt.ShouldBe(Start);
        change.Page.ParentId.ShouldBeNull();
        change.Conflict.ShouldBeFalse();
        (await RowAsync(change.Page.Id)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_page_can_go_after_before_or_between_its_neighbours()
    {
        var a = await CreateAsync("A");
        var c = await CreateAsync("C");

        await CreateAsync("B", afterId: a, beforeId: c);
        await CreateAsync("A2", afterId: a);
        await CreateAsync("Z", beforeId: a);

        (await OutlineAsync()).ShouldBe(["Z", "A", "A2", "B", "C"]);
    }

    [Fact]
    public async Task A_page_can_be_created_under_another_in_any_place()
    {
        var parent = await CreateAsync("Parent");
        var first = await CreateAsync("First", parentId: parent);
        await CreateAsync("Last", parentId: parent);
        await CreateAsync("Middle", parentId: parent, afterId: first);

        (await OutlineAsync()).ShouldBe(["Parent", "  First", "  Middle", "  Last"]);
    }

    [Fact]
    public async Task Titles_are_trimmed_and_may_be_empty()
    {
        await CreateAsync("  Spaces around  ");
        await CreateAsync("");
        var change = await Succeeded(WithTree(tree => tree.CreateAsync(WorkspaceId, null, AtTheEnd)));

        (await OutlineAsync()).ShouldBe(["Spaces around", "", ""]);
        change.Page.Title.ShouldBe("");
    }

    [Fact]
    public async Task A_title_of_five_hundred_characters_is_fine_and_one_more_is_not()
    {
        await CreateAsync(new string('x', 500));

        var error = await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, new string('x', 501), AtTheEnd)));

        error.ShouldBe(new TreeError(400, "title_invalid", "title"));
        (await TreeAsync()).Pages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_parent_from_another_workspace_or_that_does_not_exist_is_invalid()
    {
        var (_, other) = await AddWorkspaceAsync("other@example.com");
        var foreign = await CreateAsync("Foreign", workspaceId: other);

        var inOther = await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "A", new Position(foreign, null, null))));
        var missing = await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "A", new Position(Guid.NewGuid(), null, null))));

        inOther.ShouldBe(new TreeError(400, "parent_invalid", "parentId"));
        missing.ShouldBe(new TreeError(400, "parent_invalid", "parentId"));
        (await TreeAsync()).Pages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_workspace_that_does_not_exist_is_not_found()
    {
        var error = await Failed(WithTree(tree => tree.CreateAsync(Guid.NewGuid(), "A", AtTheEnd)));

        error.ShouldBe(new TreeError(404, "not_found"));
    }

    [Fact]
    public async Task A_parent_in_the_trash_is_refused()
    {
        var parent = await CreateAsync("Parent");
        await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        var error = await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "Child", new Position(parent, null, null))));

        error.ShouldBe(new TreeError(409, "parent_in_trash", "parentId"));
    }

    [Fact]
    public async Task A_parent_below_a_page_in_the_trash_is_refused_too()
    {
        var grandparent = await CreateAsync("Grandparent");
        var parent = await CreateAsync("Parent", parentId: grandparent);
        await Succeeded(WithTree(tree => tree.TrashAsync(grandparent, null)));

        var error = await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "Child", new Position(parent, null, null))));

        error.Code.ShouldBe("parent_in_trash");
    }

    [Fact]
    public async Task Neighbours_must_be_live_children_of_the_parent_and_next_to_each_other()
    {
        var parent = await CreateAsync("Parent");
        var a = await CreateAsync("A", parentId: parent);
        var b = await CreateAsync("B", parentId: parent);
        var c = await CreateAsync("C", parentId: parent);
        var elsewhere = await CreateAsync("Elsewhere");
        var trashed = await CreateAsync("Trashed", parentId: parent);
        await Succeeded(WithTree(tree => tree.TrashAsync(trashed, null)));
        var invalid = new TreeError(400, "position_invalid", "position");

        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, elsewhere, null))))).ShouldBe(invalid); // not a child
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, null, elsewhere))))).ShouldBe(invalid);
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, trashed, null))))).ShouldBe(invalid); // trashed
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, Guid.NewGuid(), null))))).ShouldBe(invalid); // unknown
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, a, c))))).ShouldBe(invalid); // not adjacent: b is between
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, b, a))))).ShouldBe(invalid); // the wrong way round
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(parent, a, a))))).ShouldBe(invalid); // the same page
        (await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(null, a, null))))).ShouldBe(invalid); // a child of parent, not of the top level
        (await TreeAsync()).Pages.Count.ShouldBe(5);
    }

    [Fact]
    public async Task A_refused_create_leaves_no_trace_not_even_a_new_tree_version()
    {
        await CreateAsync("A");
        var before = (await TreeAsync()).TreeVersion;

        await Failed(WithTree(tree => tree.CreateAsync(WorkspaceId, "x", new Position(Guid.NewGuid(), null, null))));

        (await TreeAsync()).TreeVersion.ShouldBe(before);
    }

    [Fact]
    public async Task Every_change_raises_the_tree_version_of_its_workspace_and_no_other()
    {
        var (_, other) = await AddWorkspaceAsync("other@example.com");
        var start = (await TreeAsync()).TreeVersion;

        var a = await CreateAsync("A");
        await Succeeded(WithTree(tree => tree.RenameAsync(a, "A2", null)));
        await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));
        await Succeeded(WithTree(tree => tree.RestoreAsync(a, null)));

        (await TreeAsync()).TreeVersion.ShouldBe(start + 4);
        (await TreeAsync(other)).TreeVersion.ShouldBe(0);
    }

    // ---- renaming: rename versus rename ------------------------------------------------------------------------------

    [Fact]
    public async Task Renaming_changes_the_title_and_raises_the_version_and_the_time()
    {
        var a = await CreateAsync("A");
        Clock.Advance(TimeSpan.FromMinutes(5));

        var change = await Succeeded(WithTree(tree => tree.RenameAsync(a, "  Renamed ", null)));

        change.Page.Title.ShouldBe("Renamed");
        change.Page.Version.ShouldBe(2);
        change.Page.UpdatedAt.ShouldBe(Start.AddMinutes(5));
        change.Conflict.ShouldBeFalse();
    }

    [Fact]
    public async Task Two_renames_from_the_same_version_the_last_one_wins_and_the_late_one_is_flagged_and_logged()
    {
        var a = await CreateAsync("Original");
        var logger = Substitute.For<ILogger<TreeService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var first = await Succeeded(WithTree(tree => tree.RenameAsync(a, "From the laptop", expectedVersion: 1)));
        var second = await Succeeded(WithTree(tree => tree.RenameAsync(a, "From the phone", expectedVersion: 1), logger));

        first.Conflict.ShouldBeFalse();
        second.Conflict.ShouldBeTrue();
        second.Page.Title.ShouldBe("From the phone");
        second.Page.Version.ShouldBe(3);
        (await RowAsync(a)).Title.ShouldBe("From the phone");
        logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)).ShouldBe(1);
    }

    [Fact]
    public async Task A_rename_that_knows_the_current_version_is_not_a_conflict()
    {
        var a = await CreateAsync("A");
        var first = await Succeeded(WithTree(tree => tree.RenameAsync(a, "B", expectedVersion: 1)));

        var second = await Succeeded(WithTree(tree => tree.RenameAsync(a, "C", expectedVersion: first.Page.Version)));

        second.Conflict.ShouldBeFalse();
    }

    [Fact]
    public async Task Renaming_a_page_that_does_not_exist_is_not_found()
    {
        (await Failed(WithTree(tree => tree.RenameAsync(Guid.NewGuid(), "x", null)))).ShouldBe(new TreeError(404, "not_found"));
    }

    [Fact]
    public async Task A_bad_title_is_refused_on_rename_too()
    {
        var a = await CreateAsync("A");

        (await Failed(WithTree(tree => tree.RenameAsync(a, new string('x', 501), null)))).Code.ShouldBe("title_invalid");
        (await RowAsync(a)).Version.ShouldBe(1);
    }

    [Fact]
    public async Task A_page_in_the_trash_cannot_be_renamed_nor_moved_until_it_is_restored()
    {
        var a = await CreateAsync("A");
        await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));

        (await Failed(WithTree(tree => tree.RenameAsync(a, "B", null)))).ShouldBe(new TreeError(409, "page_in_trash"));
        (await Failed(WithTree(tree => tree.MoveAsync(a, AtTheEnd, null)))).ShouldBe(new TreeError(409, "page_in_trash"));
    }

    [Fact]
    public async Task A_page_below_a_page_in_the_trash_cannot_be_renamed_nor_moved_either()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parentId: parent);
        await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        (await Failed(WithTree(tree => tree.RenameAsync(child, "x", null)))).Code.ShouldBe("page_in_trash");
        (await Failed(WithTree(tree => tree.MoveAsync(child, AtTheEnd, null)))).Code.ShouldBe("page_in_trash");
    }

    // ---- moving: move versus move ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_can_be_moved_within_its_siblings()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        await Succeeded(WithTree(tree => tree.MoveAsync(c, new Position(null, null, a), null)));
        (await OutlineAsync()).ShouldBe(["C", "A", "B"]);

        await Succeeded(WithTree(tree => tree.MoveAsync(c, new Position(null, b, null), null)));
        (await OutlineAsync()).ShouldBe(["A", "B", "C"]);

        await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(null, b, c), null)));
        (await OutlineAsync()).ShouldBe(["B", "A", "C"]);

        await Succeeded(WithTree(tree => tree.MoveAsync(b, AtTheEnd, null)));
        (await OutlineAsync()).ShouldBe(["A", "C", "B"]);
    }

    [Fact]
    public async Task A_page_can_be_nested_under_another_and_back_to_the_top()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C", parentId: b);

        await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(b, null, c), null)));
        (await OutlineAsync()).ShouldBe(["B", "  A", "  C"]);

        await Succeeded(WithTree(tree => tree.MoveAsync(c, new Position(null, null, b), null)));
        (await OutlineAsync()).ShouldBe(["C", "B", "  A"]);
    }

    [Fact]
    public async Task Moving_a_page_takes_its_whole_subtree_with_it_and_writes_one_row()
    {
        var a = await CreateAsync("A");
        var a1 = await CreateAsync("A1", parentId: a);
        await CreateAsync("A1a", parentId: a1);
        var b = await CreateAsync("B");
        await CreateAsync("B1", parentId: b);
        var before = await AllRowsAsync();

        await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(b, null, null), null)));

        (await OutlineAsync()).ShouldBe(["B", "  B1", "  A", "    A1", "      A1a"]);
        var after = await AllRowsAsync();
        after.Where(row => Differs(before.Single(old => old.Id == row.Id), row)).Select(row => row.Id).ShouldBe([a]);
    }

    private static bool Differs(Inkpact.Data.Entities.Page x, Inkpact.Data.Entities.Page y) =>
        x.ParentId != y.ParentId || x.SortKey != y.SortKey || x.Title != y.Title || x.Version != y.Version || x.DeletedAt != y.DeletedAt || x.UpdatedAt != y.UpdatedAt;

    [Fact]
    public async Task Moving_never_renumbers_the_siblings()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            ids.Add(await CreateAsync($"P{i}"));
        }

        var before = (await AllRowsAsync()).ToDictionary(p => p.Id, p => p.SortKey);

        // Drag the last one to the front, the first to the back, and one into the middle.
        await Succeeded(WithTree(tree => tree.MoveAsync(ids[11], new Position(null, null, ids[0]), null)));
        await Succeeded(WithTree(tree => tree.MoveAsync(ids[0], AtTheEnd, null)));
        await Succeeded(WithTree(tree => tree.MoveAsync(ids[5], new Position(null, ids[7], ids[8]), null)));

        var after = (await AllRowsAsync()).ToDictionary(p => p.Id, p => p.SortKey);
        var moved = new[] { ids[11], ids[0], ids[5] };
        foreach (var id in ids.Except(moved))
        {
            after[id].ShouldBe(before[id], "the key of an unmoved page changed");
        }
    }

    [Fact]
    public async Task A_move_into_the_pages_own_subtree_is_refused()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B", parentId: a);
        var c = await CreateAsync("C", parentId: b);

        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(a, null, null), null)))).ShouldBe(new TreeError(409, "move_cycle", "parentId"));
        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(b, null, null), null)))).Code.ShouldBe("move_cycle");
        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(c, null, null), null)))).Code.ShouldBe("move_cycle");
        (await Failed(WithTree(tree => tree.MoveAsync(b, new Position(c, null, null), null)))).Code.ShouldBe("move_cycle");
        (await OutlineAsync()).ShouldBe(["A", "  B", "    C"]);
    }

    [Fact]
    public async Task Moving_to_the_parent_it_already_has_is_fine()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B", parentId: a);
        var c = await CreateAsync("C", parentId: a);

        await Succeeded(WithTree(tree => tree.MoveAsync(c, new Position(a, null, b), null)));

        (await OutlineAsync()).ShouldBe(["A", "  C", "  B"]);
    }

    [Fact]
    public async Task Two_moves_one_after_the_other_the_last_one_wins()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(b, null, null), expectedVersion: 1)));
        var late = await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(c, null, null), expectedVersion: 1)));

        late.Conflict.ShouldBeTrue();
        (await OutlineAsync()).ShouldBe(["B", "C", "  A"]);
    }

    [Fact]
    public async Task A_move_into_a_parent_in_the_trash_is_refused_and_the_page_stays_where_it_was()
    {
        var a = await CreateAsync("A");
        var parent = await CreateAsync("Parent");
        await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        var error = await Failed(WithTree(tree => tree.MoveAsync(a, new Position(parent, null, null), null)));

        error.ShouldBe(new TreeError(409, "parent_in_trash", "parentId"));
        (await OutlineAsync()).ShouldBe(["A"]);
        (await RowAsync(a)).Version.ShouldBe(1);
    }

    [Fact]
    public async Task A_move_to_another_workspace_is_not_possible_yet()
    {
        var a = await CreateAsync("A");
        var (_, other) = await AddWorkspaceAsync("other@example.com");
        var foreign = await CreateAsync("Foreign", workspaceId: other);

        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(foreign, null, null), null)))).ShouldBe(new TreeError(400, "parent_invalid", "parentId"));
    }

    [Fact]
    public async Task Positions_in_a_move_follow_the_same_rules_as_in_a_create()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");
        var invalid = new TreeError(400, "position_invalid", "position");

        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(null, a, null), null)))).ShouldBe(invalid); // after itself
        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(null, null, a), null)))).ShouldBe(invalid); // before itself
        (await Failed(WithTree(tree => tree.MoveAsync(a, new Position(null, Guid.NewGuid(), null), null)))).ShouldBe(invalid);
        // Between b and c, as they are once a has left: b and c are next to each other.
        await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(null, b, c), null)));
        (await OutlineAsync()).ShouldBe(["B", "A", "C"]);
    }

    [Fact]
    public async Task Moving_a_page_that_does_not_exist_is_not_found()
    {
        (await Failed(WithTree(tree => tree.MoveAsync(Guid.NewGuid(), AtTheEnd, null)))).ShouldBe(new TreeError(404, "not_found"));
    }

    // ---- trash and restore -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Trashing_a_page_hides_its_subtree_and_marks_only_the_page()
    {
        var a = await CreateAsync("A");
        var a1 = await CreateAsync("A1", parentId: a);
        var a1a = await CreateAsync("A1a", parentId: a1);
        await CreateAsync("B");

        var change = await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));

        (await OutlineAsync()).ShouldBe(["B"]);
        change.Page.DeletedAt.ShouldBe(Start);
        change.Page.Version.ShouldBe(2);
        (await RowAsync(a1)).DeletedAt.ShouldBeNull(); // children carry no mark of their own
        (await RowAsync(a1a)).DeletedAt.ShouldBeNull();
        (await RowAsync(a1)).Version.ShouldBe(1);
    }

    [Fact]
    public async Task The_trash_lists_only_the_top_of_what_was_trashed()
    {
        var a = await CreateAsync("A");
        var a1 = await CreateAsync("A1", parentId: a);
        var b = await CreateAsync("B");
        Clock.Advance(TimeSpan.FromMinutes(1));
        await Succeeded(WithTree(tree => tree.TrashAsync(a1, null)));
        Clock.Advance(TimeSpan.FromMinutes(1));
        await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));
        Clock.Advance(TimeSpan.FromMinutes(1));
        await Succeeded(WithTree(tree => tree.TrashAsync(b, null)));

        var trash = await WithTree(tree => tree.GetTrashAsync(WorkspaceId));

        // a1 is in the trash too, but below a, which is what a person restores. Newest first.
        trash.Select(p => p.Title).ShouldBe(["B", "A"]);
        trash.ShouldAllBe(p => p.DeletedAt != null);
    }

    [Fact]
    public async Task The_trash_of_one_workspace_never_shows_another()
    {
        var (_, other) = await AddWorkspaceAsync("other@example.com");
        var foreign = await CreateAsync("Foreign", workspaceId: other);
        await Succeeded(WithTree(tree => tree.TrashAsync(foreign, null)));

        (await WithTree(tree => tree.GetTrashAsync(WorkspaceId))).ShouldBeEmpty();
        (await WithTree(tree => tree.GetTrashAsync(other))).Select(p => p.Id).ShouldBe([foreign]);
    }

    [Fact]
    public async Task Trashing_something_already_in_the_trash_changes_nothing()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parentId: parent);
        var first = await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        var again = await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));
        var below = await Succeeded(WithTree(tree => tree.TrashAsync(child, null)));

        again.Page.Version.ShouldBe(first.Page.Version);
        below.Page.DeletedAt.ShouldBeNull(); // hidden through the parent, no mark of its own
        (await RowAsync(child)).DeletedAt.ShouldBeNull();
        (await RowAsync(child)).Version.ShouldBe(1);
        (await WithTree(tree => tree.GetTrashAsync(WorkspaceId))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Restoring_a_parent_brings_its_children_back_with_it()
    {
        var a = await CreateAsync("A");
        var a1 = await CreateAsync("A1", parentId: a);
        await CreateAsync("A1a", parentId: a1);
        await CreateAsync("A2", parentId: a);
        await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(a, null)));

        change.RestoredTo.ShouldBe("original");
        change.Page.DeletedAt.ShouldBeNull();
        change.Page.Version.ShouldBe(3);
        (await OutlineAsync()).ShouldBe(["A", "  A1", "    A1a", "  A2"]);
    }

    [Fact]
    public async Task A_child_trashed_before_its_parent_stays_in_the_trash_when_the_parent_comes_back()
    {
        var a = await CreateAsync("A");
        var a1 = await CreateAsync("A1", parentId: a);
        await CreateAsync("A2", parentId: a);
        await Succeeded(WithTree(tree => tree.TrashAsync(a1, null)));
        await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));

        await Succeeded(WithTree(tree => tree.RestoreAsync(a, null)));

        (await OutlineAsync()).ShouldBe(["A", "  A2"]);
        (await WithTree(tree => tree.GetTrashAsync(WorkspaceId))).Select(p => p.Id).ShouldBe([a1]);
    }

    [Fact]
    public async Task A_restored_page_goes_back_between_the_same_neighbours()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");
        await Succeeded(WithTree(tree => tree.TrashAsync(b, null)));
        await CreateAsync("Newer"); // goes last, so the old key of b is still free

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(b, null)));

        change.RestoredTo.ShouldBe("original");
        (await OutlineAsync()).ShouldBe(["A", "B", "C", "Newer"]);
    }

    [Fact]
    public async Task When_the_old_key_is_taken_the_page_goes_right_after_the_nearest_key_below_it()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");
        var bKey = (await RowAsync(b)).SortKey;
        await Succeeded(WithTree(tree => tree.TrashAsync(b, null)));
        // With b gone, a page between a and c gets exactly the key b had.
        var newcomer = await CreateAsync("Newcomer", afterId: a, beforeId: c);
        (await RowAsync(newcomer)).SortKey.ShouldBe(bKey);

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(b, null)));

        change.RestoredTo.ShouldBe("original");
        (await OutlineAsync()).ShouldBe(["A", "B", "Newcomer", "C"]);
        (await RowAsync(b)).SortKey.ShouldNotBe(bKey);
    }

    [Fact]
    public async Task A_page_restored_into_a_parent_that_is_in_the_trash_goes_to_the_end_of_the_top_level()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parentId: parent);
        await CreateAsync("Other");
        await Succeeded(WithTree(tree => tree.TrashAsync(child, null)));
        await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(child, null)));

        change.RestoredTo.ShouldBe("root");
        change.Page.ParentId.ShouldBeNull();
        (await OutlineAsync()).ShouldBe(["Other", "Child"]);
        // The parent is still in the trash, and so is nothing else.
        (await WithTree(tree => tree.GetTrashAsync(WorkspaceId))).Select(p => p.Id).ShouldBe([parent]);
    }

    [Fact]
    public async Task A_page_whose_parent_is_hidden_by_a_trashed_grandparent_also_goes_to_the_top()
    {
        var grandparent = await CreateAsync("Grandparent");
        var parent = await CreateAsync("Parent", parentId: grandparent);
        var child = await CreateAsync("Child", parentId: parent);
        await Succeeded(WithTree(tree => tree.TrashAsync(child, null)));
        await Succeeded(WithTree(tree => tree.TrashAsync(grandparent, null)));

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(child, null)));

        change.RestoredTo.ShouldBe("root");
        (await OutlineAsync()).ShouldBe(["Child"]);
    }

    [Fact]
    public async Task Restore_versus_move_the_parent_moved_meanwhile_and_the_child_follows_its_parent()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var child = await CreateAsync("Child", parentId: a);
        await Succeeded(WithTree(tree => tree.TrashAsync(child, null)));
        await Succeeded(WithTree(tree => tree.MoveAsync(a, new Position(b, null, null), null)));

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(child, null)));

        change.RestoredTo.ShouldBe("original");
        (await OutlineAsync()).ShouldBe(["B", "  A", "    Child"]);
    }

    [Fact]
    public async Task A_page_that_is_only_hidden_by_its_parent_cannot_be_restored_on_its_own()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parentId: parent);
        await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        (await Failed(WithTree(tree => tree.RestoreAsync(child, null)))).ShouldBe(new TreeError(409, "restore_the_parent"));
    }

    [Fact]
    public async Task Restoring_a_page_that_is_not_in_the_trash_changes_nothing()
    {
        var a = await CreateAsync("A");

        var change = await Succeeded(WithTree(tree => tree.RestoreAsync(a, null)));

        change.Page.Version.ShouldBe(1);
        change.RestoredTo.ShouldBeNull();
    }

    [Fact]
    public async Task Trashing_or_restoring_a_page_that_does_not_exist_is_not_found()
    {
        (await Failed(WithTree(tree => tree.TrashAsync(Guid.NewGuid(), null)))).ShouldBe(new TreeError(404, "not_found"));
        (await Failed(WithTree(tree => tree.RestoreAsync(Guid.NewGuid(), null)))).ShouldBe(new TreeError(404, "not_found"));
    }

    [Fact]
    public async Task A_trashed_page_can_be_restored_and_trashed_again_and_again()
    {
        var a = await CreateAsync("A");
        for (var round = 0; round < 3; round++)
        {
            await Succeeded(WithTree(tree => tree.TrashAsync(a, null)));
            (await OutlineAsync()).ShouldBeEmpty();
            await Succeeded(WithTree(tree => tree.RestoreAsync(a, null)));
            (await OutlineAsync()).ShouldBe(["A"]);
        }

        (await RowAsync(a)).Version.ShouldBe(7);
    }

    [Fact]
    public async Task Trash_and_restore_carry_the_version_conflict_flag_too()
    {
        var a = await CreateAsync("A");
        await Succeeded(WithTree(tree => tree.RenameAsync(a, "B", null)));

        (await Succeeded(WithTree(tree => tree.TrashAsync(a, expectedVersion: 1)))).Conflict.ShouldBeTrue();
        (await Succeeded(WithTree(tree => tree.RestoreAsync(a, expectedVersion: 3)))).Conflict.ShouldBeFalse();
    }

    // ---- reading -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tree_lists_parents_before_children_and_siblings_by_key_as_bytes()
    {
        // Upper case sorts before lower case in a byte comparison, but not in most languages: the order must be the bytes'.
        var upper = await CreateAsync("Upper");
        await CreateAsync("Next");
        var lower = await CreateAsync("Lower");
        await Succeeded(WithTree(tree => tree.MoveAsync(lower, new Position(null, null, upper), null)));
        await Succeeded(WithTree(tree => tree.MoveAsync(upper, new Position(null, null, lower), null)));
        var keys = (await TreeAsync()).Pages.Select(p => p.SortKey).ToList();

        keys.ShouldBe(keys.Order(StringComparer.Ordinal).ToList());
        keys.Distinct().Count().ShouldBe(keys.Count);
    }

    [Fact]
    public async Task One_page_can_be_read_with_whether_it_is_in_the_trash()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parentId: parent);

        (await WithTree(tree => tree.GetPageAsync(child)))!.Value.InTrash.ShouldBeFalse();
        await Succeeded(WithTree(tree => tree.TrashAsync(parent, null)));

        (await WithTree(tree => tree.GetPageAsync(parent)))!.Value.InTrash.ShouldBeTrue();
        (await WithTree(tree => tree.GetPageAsync(child)))!.Value.InTrash.ShouldBeTrue();
        (await WithTree(tree => tree.GetPageAsync(Guid.NewGuid()))).ShouldBeNull();
    }

    [Fact]
    public async Task The_tree_of_one_workspace_never_shows_the_pages_of_another()
    {
        var (_, other) = await AddWorkspaceAsync("other@example.com");
        await CreateAsync("Mine");
        await CreateAsync("Theirs", workspaceId: other);

        (await TreeAsync()).Pages.Select(p => p.Title).ShouldBe(["Mine"]);
        (await TreeAsync(other)).Pages.Select(p => p.Title).ShouldBe(["Theirs"]);
    }

    [Fact]
    public async Task Two_workspaces_may_use_the_same_keys()
    {
        var (_, other) = await AddWorkspaceAsync("other@example.com");
        var mine = await CreateAsync("Mine");
        var theirs = await CreateAsync("Theirs", workspaceId: other);

        (await RowAsync(mine)).SortKey.ShouldBe((await RowAsync(theirs)).SortKey);
    }

    [Fact]
    public async Task Two_live_siblings_cannot_share_a_key_the_database_refuses()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        await using var db = CreateContext();
        var page = await db.Pages.FindAsync(b);
        page!.SortKey = (await RowAsync(a)).SortKey;

        await Should.ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_trashed_page_may_keep_the_key_of_a_live_one()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        await Succeeded(WithTree(tree => tree.TrashAsync(b, null)));
        await using var db = CreateContext();
        (await db.Pages.FindAsync(b))!.SortKey = (await RowAsync(a)).SortKey;

        await db.SaveChangesAsync(); // no exception
    }

    [Fact]
    public async Task Two_pages_at_the_top_level_cannot_share_a_key_either()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        await using var db = CreateContext();
        (await db.Pages.FindAsync(b))!.SortKey = (await RowAsync(a)).SortKey;

        // The parent is null for both: the index treats two nulls as equal.
        await Should.ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync());
    }
}
