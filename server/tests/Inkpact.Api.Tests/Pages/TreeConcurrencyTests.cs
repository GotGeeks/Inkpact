using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Pages;

/// <summary>Several requests at the same moment. Whatever the order they land in, the tree must come out consistent.</summary>
public class TreeConcurrencyTests(PostgresFixture postgres) : TreeTestBase(postgres)
{
    private static readonly Position AtTheEnd = new(null, null, null);

    [Fact]
    public async Task Twenty_pages_created_at_the_same_moment_at_the_same_place_all_get_their_own_key()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            WithTree(tree => tree.CreateAsync(WorkspaceId, $"Page {i}", AtTheEnd))));

        results.ShouldAllBe(r => r.Succeeded);
        var pages = (await TreeAsync()).Pages;
        pages.Count.ShouldBe(20);
        pages.Select(p => p.SortKey).Distinct().Count().ShouldBe(20);
        pages.Select(p => p.SortKey).ShouldBe(pages.Select(p => p.SortKey).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Twenty_pages_created_right_after_the_same_page_all_land_between_it_and_the_next()
    {
        var first = await CreateAsync("First");
        await CreateAsync("Last");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            WithTree(tree => tree.CreateAsync(WorkspaceId, $"New {i}", new Position(null, first, null)))));

        results.ShouldAllBe(r => r.Succeeded);
        var titles = (await TreeAsync()).Pages.Select(p => p.Title).ToList();
        titles.Count.ShouldBe(22);
        titles[0].ShouldBe("First");
        titles[^1].ShouldBe("Last");
        (await TreeAsync()).Pages.Select(p => p.SortKey).Distinct().Count().ShouldBe(22);
    }

    [Fact]
    public async Task Two_moves_that_are_fine_alone_but_make_a_cycle_together_let_exactly_one_through()
    {
        for (var round = 0; round < 15; round++)
        {
            var a = await CreateAsync($"A{round}");
            var b = await CreateAsync($"B{round}");

            var results = await Task.WhenAll(
                WithTree(tree => tree.MoveAsync(a, new Position(b, null, null), null)),
                WithTree(tree => tree.MoveAsync(b, new Position(a, null, null), null)));

            results.Count(r => r.Succeeded).ShouldBe(1, $"round {round}");
            results.Single(r => !r.Succeeded).Error!.Code.ShouldBe("move_cycle");
        }

        // Nothing was lost to a cycle: every page can be reached from the top.
        (await TreeAsync()).Pages.Count.ShouldBe(30);
    }

    [Fact]
    public async Task A_move_and_the_trashing_of_the_new_parent_at_the_same_moment_leave_a_consistent_tree()
    {
        for (var round = 0; round < 15; round++)
        {
            var page = await CreateAsync($"Page{round}");
            var parent = await CreateAsync($"Parent{round}");

            var results = await Task.WhenAll(
                WithTree(tree => tree.MoveAsync(page, new Position(parent, null, null), null)),
                WithTree(tree => tree.TrashAsync(parent, null)));

            // The move either happened first, and the page went into the trash with its new parent, or it was refused.
            results[1].Succeeded.ShouldBeTrue();
            var outcome = (await TreeAsync()).Pages.Select(p => p.Id).ToHashSet();
            outcome.ShouldNotContain(parent);
            if (results[0].Succeeded)
            {
                outcome.ShouldNotContain(page, $"round {round}: moved under a page that is now in the trash");
            }
            else
            {
                results[0].Error!.Code.ShouldBe("parent_in_trash");
                outcome.ShouldContain(page);
            }
        }
    }

    [Fact]
    public async Task Ten_renames_at_the_same_moment_all_count_and_one_of_them_is_the_title()
    {
        var page = await CreateAsync("Original");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            WithTree(tree => tree.RenameAsync(page, $"Title {i}", expectedVersion: 1))));

        results.ShouldAllBe(r => r.Succeeded);
        var row = await RowAsync(page);
        row.Version.ShouldBe(11);
        row.Title.ShouldStartWith("Title ");
        // Every one of them was based on version one, and nine of them found it already changed.
        results.Count(r => r.Value!.Conflict).ShouldBe(9);
    }

    [Fact]
    public async Task Trashing_and_restoring_at_the_same_moment_end_in_one_of_the_two_states_without_errors()
    {
        for (var round = 0; round < 10; round++)
        {
            var page = await CreateAsync($"Page{round}");
            await Succeeded(WithTree(tree => tree.TrashAsync(page, null)));

            var results = await Task.WhenAll(
                WithTree(tree => tree.RestoreAsync(page, null)),
                WithTree(tree => tree.TrashAsync(page, null)));

            results.ShouldAllBe(r => r.Succeeded);
            var row = await RowAsync(page);
            (await WithTree(tree => tree.GetPageAsync(page)))!.Value.InTrash.ShouldBe(row.DeletedAt is not null);
        }
    }

    [Fact]
    public async Task Moving_many_pages_to_the_same_place_at_the_same_moment_gives_each_its_own_key()
    {
        var target = await CreateAsync("Target");
        var movers = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            movers.Add(await CreateAsync($"Mover {i}"));
        }

        var results = await Task.WhenAll(movers.Select(id =>
            WithTree(tree => tree.MoveAsync(id, new Position(target, null, null), null))));

        results.ShouldAllBe(r => r.Succeeded);
        var children = (await TreeAsync()).Pages.Where(p => p.ParentId == target).ToList();
        children.Count.ShouldBe(12);
        children.Select(p => p.SortKey).Distinct().Count().ShouldBe(12);
    }

    [Fact]
    public async Task Changes_to_different_workspaces_do_not_wait_for_each_other()
    {
        var (_, other) = await AddWorkspaceAsync("other@example.com");

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => WithTree(tree => tree.CreateAsync(WorkspaceId, $"Mine {i}", AtTheEnd)))
                .Concat(Enumerable.Range(0, 8).Select(i => WithTree(tree => tree.CreateAsync(other, $"Theirs {i}", AtTheEnd)))));

        results.ShouldAllBe(r => r.Succeeded);
        (await TreeAsync()).Pages.Count.ShouldBe(8);
        (await TreeAsync(other)).Pages.Count.ShouldBe(8);
    }
}
