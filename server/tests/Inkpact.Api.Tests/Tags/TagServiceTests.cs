using Inkpact.Api.Pages;
using Inkpact.Api.Tags;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Tags;

/// <summary>
/// A page's tags as a set (run B.2): add by name (an existing tag's spelling kept, or a new tag made), remove by id,
/// both in one request with the remove applied first. The real database.
/// </summary>
public class TagServiceTests(PostgresFixture postgres) : TagTestBase(postgres)
{
    [Fact]
    public async Task Adding_a_new_name_makes_a_tag_and_adding_it_again_does_nothing()
    {
        var page = await CreatePageAsync();

        var first = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])));
        var second = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])));

        first.Select(t => t.Name).ShouldBe(["Work"]);
        second.Select(t => t.Name).ShouldBe(["Work"]);
        (await AllTagsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Adding_a_name_that_matches_an_existing_tag_in_a_different_case_keeps_the_existing_spelling()
    {
        var page = await CreatePageAsync();
        await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])));

        var result = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["WORK"], [])));

        result.Select(t => t.Name).ShouldBe(["Work"]);
        (await AllTagsAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Removing_by_id_takes_the_tag_off_the_page_and_removing_again_does_nothing()
    {
        var page = await CreatePageAsync();
        var tag = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])))).Single();

        var first = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, [], [tag.Id])));
        var second = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, [], [tag.Id])));

        first.ShouldBeEmpty();
        second.ShouldBeEmpty();
        // The tag itself is still there, so typing its name again reuses it rather than making a duplicate.
        (await AllTagsAsync()).Select(t => t.Name).ShouldBe(["Work"]);
    }

    [Fact]
    public async Task Removing_an_id_the_page_does_not_have_does_nothing()
    {
        var page = await CreatePageAsync();
        var otherPage = await CreatePageAsync("Other");
        var otherTag = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(otherPage, ["Home"], [])))).Single();

        var result = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, [], [otherTag.Id, Guid.NewGuid()])));

        result.ShouldBeEmpty();
        (await PageTagIdsAsync(otherPage)).ShouldBe([otherTag.Id]);
    }

    [Fact]
    public async Task Both_in_one_request_removes_first_then_adds()
    {
        var page = await CreatePageAsync();
        var work = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])))).Single();

        var result = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Home"], [work.Id])));

        result.Select(t => t.Name).ShouldBe(["Home"]);
    }

    [Fact]
    public async Task A_name_is_cleaned_by_the_imports_rule_trimmed_with_control_characters_out_and_tabs_as_spaces()
    {
        var page = await CreatePageAsync();

        var result = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["  book\u0000\tclub  "], [])));

        result.Select(t => t.Name).ShouldBe(["book club"]);
    }

    [Fact]
    public async Task A_name_with_inner_spaces_finds_the_tag_the_import_made_with_the_same_name()
    {
        // The import keeps inner spaces as they are (TagNames.Clean): a name typed with them must find that tag.
        var page = await CreatePageAsync();
        var imported = await AddTagAsync("to  do");

        var result = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["To  Do"], [])));

        result.ShouldBe([new TagDto(imported, "to  do")]);
        (await AllTagsAsync()).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_name_is_refused(string name)
    {
        var page = await CreatePageAsync();

        var error = await FailedAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, [name], [])));

        error.Code.ShouldBe("tag_name_empty");
    }

    [Fact]
    public async Task A_name_over_the_limit_is_refused()
    {
        var page = await CreatePageAsync();

        var error = await FailedAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, [new string('a', TagService.MaxNameLength + 1)], [])));

        error.Code.ShouldBe("tag_name_too_long");
    }

    [Fact]
    public async Task More_than_the_limit_of_names_and_ids_together_is_refused()
    {
        var page = await CreatePageAsync();
        var names = Enumerable.Range(0, TagService.MaxNamesPerRequest + 1).Select(i => $"t{i}").ToList();

        var error = await FailedAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, names, [])));

        error.Code.ShouldBe("too_many_tags");
        (await AllTagsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_in_the_trash_or_below_one_refuses_the_change()
    {
        var parent = await CreatePageAsync("Parent");
        var below = await CreatePageAsync("Below", parent);
        await TrashPageAsync(parent);

        var direct = await FailedAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(parent, ["Work"], [])));
        var underTrash = await FailedAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(below, ["Work"], [])));

        direct.Code.ShouldBe("page_in_trash");
        underTrash.Code.ShouldBe("page_in_trash");
    }

    [Fact]
    public async Task A_page_that_does_not_exist_is_not_found()
    {
        var error = await FailedAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(Guid.NewGuid(), ["Work"], [])));

        error.Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task A_pages_tags_are_sorted_by_their_lower_case_name()
    {
        var page = await CreatePageAsync();

        var result = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["banana", "Apple", "cherry"], [])));

        result.Select(t => t.Name).ShouldBe(["Apple", "banana", "cherry"]);
    }

    // ---- concurrency ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ten_requests_on_one_page_with_different_tags_at_the_same_moment_all_land()
    {
        var page = await CreatePageAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            WithTagsAsync(tags => tags.ChangeTagsAsync(page, [$"tag{i}"], []))));

        results.ShouldAllBe(r => r.Succeeded);
        (await PageTagIdsAsync(page)).Count.ShouldBe(10);
    }

    [Fact]
    public async Task Two_pages_making_the_same_new_tag_at_the_same_moment_end_with_one_tag()
    {
        var pages = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => CreatePageAsync($"Page {i}")));

        var results = await Task.WhenAll(pages.Select(page => WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Same"], []))));

        results.ShouldAllBe(r => r.Succeeded);
        var tags = await AllTagsAsync();
        tags.Count.ShouldBe(1);
        tags.Single().Name.ShouldBe("Same");
        foreach (var page in pages)
        {
            (await PageTagIdsAsync(page)).ShouldBe([tags.Single().Id]);
        }
    }

    [Fact]
    public async Task Renaming_a_tag_is_fine_for_a_change_of_case_only_and_refused_when_another_tag_has_the_name()
    {
        var page = await CreatePageAsync();
        var (work, home) = await WithTagsAsync(async tags =>
        {
            var added = await tags.ChangeTagsAsync(page, ["Work", "Home"], []);
            return (added.Value!.Single(t => t.Name == "Work"), added.Value!.Single(t => t.Name == "Home"));
        });

        var caseOnly = await WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, work.Id, "WORK"));
        var taken = await WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, work.Id, "Home"));
        var renamed = await WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, work.Id, "Job"));

        caseOnly.Succeeded.ShouldBeTrue();
        caseOnly.Value!.Name.ShouldBe("WORK");
        taken.Error!.Code.ShouldBe("tag_name_taken");
        renamed.Succeeded.ShouldBeTrue();
        renamed.Value!.Name.ShouldBe("Job");
        (await AllTagsAsync()).Select(t => t.Name).ShouldBe(["Home", "Job"], ignoreOrder: true);
    }

    [Fact]
    public async Task Renaming_a_tag_of_another_workspace_or_one_that_does_not_exist_is_not_found()
    {
        var page = await CreatePageAsync();
        var tag = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])))).Single();

        var otherWorkspace = await WithTagsAsync(tags => tags.RenameTagAsync(Guid.NewGuid(), tag.Id, "Job"));
        var noSuchTag = await WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, Guid.NewGuid(), "Job"));

        otherWorkspace.Error!.Code.ShouldBe("not_found");
        noSuchTag.Error!.Code.ShouldBe("not_found");
    }

    [Fact]
    public async Task The_workspace_list_counts_live_pages_only_and_leaves_out_a_tag_on_none()
    {
        var live = await CreatePageAsync("Live");
        var trashed = await CreatePageAsync("Trashed");
        await WithTagsAsync(tags => tags.ChangeTagsAsync(live, ["Work"], []));
        await WithTagsAsync(tags => tags.ChangeTagsAsync(trashed, ["Work", "Gone"], []));
        await TrashPageAsync(trashed);

        var list = await WithTagsAsync(tags => tags.GetWorkspaceTagsAsync(WorkspaceId));

        list.Select(t => (t.Name, t.PageCount)).ShouldBe([("Work", 1)]);
    }

    [Fact]
    public async Task The_pages_of_a_tag_are_the_live_ones_by_title_with_the_parents_title()
    {
        var notebook = await CreatePageAsync("Notebook");
        var banana = await CreatePageAsync("banana", notebook);
        var apple = await CreatePageAsync("Apple");
        var trashed = await CreatePageAsync("Zed");
        foreach (var page in new[] { banana, apple, trashed })
        {
            await WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], []));
        }

        await TrashPageAsync(trashed);
        var tagId = (await WithTagsAsync(tags => tags.GetWorkspaceTagsAsync(WorkspaceId))).Single().Id;

        var result = await WithTagsAsync(tags => tags.GetTagPagesAsync(WorkspaceId, tagId));

        result.Value!.Items.ShouldBe(
        [
            new ListedPage(apple, "Apple", null),
            new ListedPage(banana, "banana", "Notebook"),
        ]);
        result.Value!.More.ShouldBeFalse();
    }

    [Fact]
    public async Task Renaming_onto_the_name_of_a_tag_no_page_has_takes_it_over_and_that_tag_goes()
    {
        var page = await CreatePageAsync();
        var work = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])))).Single();
        var unused = await AddTagAsync("projects");

        var renamed = await WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, work.Id, "Projects"));

        renamed.Value.ShouldBe(new TagDto(work.Id, "Projects"));
        (await AllTagsAsync()).Select(t => t.Id).ShouldBe([work.Id]);
        (await AllTagsAsync()).ShouldNotContain(t => t.Id == unused);
    }

    [Fact]
    public async Task Renaming_onto_the_name_of_a_tag_only_pages_in_the_trash_have_says_so()
    {
        var page = await CreatePageAsync("Live");
        var trashed = await CreatePageAsync("Trashed");
        var work = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])))).Single();
        await WithTagsAsync(tags => tags.ChangeTagsAsync(trashed, ["Projects"], []));
        await TrashPageAsync(trashed);

        var result = await WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, work.Id, "projects"));

        result.Error!.Code.ShouldBe("tag_name_taken_in_trash");
        (await AllTagsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Two_renames_to_one_new_name_at_the_same_moment_let_one_through_and_refuse_the_other()
    {
        for (var round = 0; round < 10; round++)
        {
            var page = await CreatePageAsync($"Page {round}");
            var added = await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, [$"a{round}", $"b{round}"], [])));

            var results = await Task.WhenAll(added.Select(tag =>
                WithTagsAsync(tags => tags.RenameTagAsync(WorkspaceId, tag.Id, $"same{round}"))));

            results.Count(r => r.Succeeded).ShouldBe(1, $"round {round}");
            results.Single(r => !r.Succeeded).Error!.Code.ShouldBe("tag_name_taken");
        }
    }

    [Fact]
    public async Task A_tags_pages_stop_at_two_hundred_by_title_and_say_there_are_more()
    {
        var tagId = await AddTagAsync("many");
        await using (var db = CreateContext())
        {
            var pages = Enumerable.Range(0, TagService.MaxTagPages + 1)
                .Select(i => new Page { WorkspaceId = WorkspaceId, Title = $"Page {TagService.MaxTagPages - i:D3}", SortKey = $"a{i:D4}", CreatedAt = Start, UpdatedAt = Start })
                .ToList();
            db.Pages.AddRange(pages);
            db.PageTags.AddRange(pages.Select(p => new PageTag { PageId = p.Id, TagId = tagId }));
            await db.SaveChangesAsync();
        }

        var result = (await WithTagsAsync(tags => tags.GetTagPagesAsync(WorkspaceId, tagId))).Value!;

        result.Items.Count.ShouldBe(TagService.MaxTagPages);
        result.More.ShouldBeTrue();
        result.Items[0].Title.ShouldBe("Page 000");
        result.Items[^1].Title.ShouldBe("Page 199");
    }

    [Fact]
    public async Task A_tags_pages_are_sorted_with_case_ignored()
    {
        var notebook = await CreatePageAsync("Notebook");
        foreach (var title in new[] { "banana", "Apple", "cherry" })
        {
            var page = await CreatePageAsync(title, notebook);
            await WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Fruit"], []));
        }

        var tagId = (await AllTagsAsync()).Single().Id;
        var result = (await WithTagsAsync(tags => tags.GetTagPagesAsync(WorkspaceId, tagId))).Value!;

        result.Items.Select(i => (i.Title, i.ParentTitle)).ShouldBe([("Apple", "Notebook"), ("banana", "Notebook"), ("cherry", "Notebook")]);
    }

    [Fact]
    public async Task A_tag_of_another_workspace_is_not_found()
    {
        var page = await CreatePageAsync();
        var tag = (await SucceededAsync(WithTagsAsync(tags => tags.ChangeTagsAsync(page, ["Work"], [])))).Single();

        var result = await WithTagsAsync(tags => tags.GetTagPagesAsync(Guid.NewGuid(), tag.Id));

        result.Error!.Code.ShouldBe("not_found");
    }
}
