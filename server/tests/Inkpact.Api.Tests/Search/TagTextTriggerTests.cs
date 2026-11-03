using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Search;

/// <summary>
/// <c>pages.tag_text</c>, which the search vectors are made from, is kept by triggers on <c>page_tags</c> and <c>tags</c>: any
/// way of changing a page's tags or a tag's name, by any code, is seen by search.
/// </summary>
public class TagTextTriggerTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private Guid _workspaceId;
    private int _keys;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = CreateContext();
        var user = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Now };
        var workspace = new Workspace { Name = "Workspace", OwnerId = user.Id, CreatedAt = Now };
        db.AddRange(user, workspace);
        await db.SaveChangesAsync();
        _workspaceId = workspace.Id;
    }

    private async Task<Guid> AddPageAsync()
    {
        await using var db = CreateContext();
        var page = new Page { WorkspaceId = _workspaceId, Title = "Page", SortKey = $"a{_keys++:D3}", CreatedAt = Now, UpdatedAt = Now };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    }

    private async Task<Guid> AddTagAsync(string name)
    {
        await using var db = CreateContext();
        var tag = new Tag { WorkspaceId = _workspaceId, Name = name, NormalizedName = name.ToLowerInvariant(), CreatedAt = Now };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        return tag.Id;
    }

    private async Task TagAsync(Guid pageId, params Guid[] tagIds)
    {
        await using var db = CreateContext();
        db.PageTags.AddRange(tagIds.Select(t => new PageTag { PageId = pageId, TagId = t }));
        await db.SaveChangesAsync();
    }

    private async Task<string> TagTextAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.Pages.Where(p => p.Id == pageId).Select(p => p.TagText).SingleAsync();
    }

    private async Task<bool> FoundAsync(Guid pageId, string query)
    {
        await using var db = CreateContext();
        return await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pages WHERE id = {pageId} AND search_vector @@ to_tsquery('inkpact_search', {query}) AND title_vector @@ to_tsquery('inkpact_search', {query})")
            .SingleAsync() == 1;
    }

    [Fact]
    public async Task Tagging_a_page_puts_the_names_in_name_order_and_the_page_is_found_by_them()
    {
        var page = await AddPageAsync();
        var (work, home) = (await AddTagAsync("Work"), await AddTagAsync("home"));

        await TagAsync(page, work, home);

        (await TagTextAsync(page)).ShouldBe("home Work");
        (await FoundAsync(page, "work:*")).ShouldBeTrue();
    }

    [Fact]
    public async Task Taking_a_tag_off_takes_its_name_out()
    {
        var page = await AddPageAsync();
        var (work, home) = (await AddTagAsync("Work"), await AddTagAsync("Home"));
        await TagAsync(page, work, home);

        await using (var db = CreateContext())
        {
            await db.PageTags.Where(t => t.PageId == page && t.TagId == work).ExecuteDeleteAsync();
        }

        (await TagTextAsync(page)).ShouldBe("Home");
        (await FoundAsync(page, "work:*")).ShouldBeFalse();
    }

    [Fact]
    public async Task Renaming_a_tag_renames_it_on_every_page_that_has_it_and_no_other()
    {
        var (first, second, other) = (await AddPageAsync(), await AddPageAsync(), await AddPageAsync());
        var (taxes, home) = (await AddTagAsync("Taxes"), await AddTagAsync("Home"));
        await TagAsync(first, taxes);
        await TagAsync(second, taxes, home);
        await TagAsync(other, home);

        await using (var db = CreateContext())
        {
            await db.Tags.Where(t => t.Id == taxes).ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "Fiscal").SetProperty(t => t.NormalizedName, "fiscal"));
        }

        (await TagTextAsync(first)).ShouldBe("Fiscal");
        (await TagTextAsync(second)).ShouldBe("Fiscal Home");
        (await TagTextAsync(other)).ShouldBe("Home");
        (await FoundAsync(second, "taxes:*")).ShouldBeFalse();
        (await FoundAsync(second, "fisc:*")).ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_a_tag_takes_it_off_its_pages()
    {
        var page = await AddPageAsync();
        var (work, home) = (await AddTagAsync("Work"), await AddTagAsync("Home"));
        await TagAsync(page, work, home);

        await using (var db = CreateContext())
        {
            await db.Tags.Where(t => t.Id == work).ExecuteDeleteAsync();
        }

        (await TagTextAsync(page)).ShouldBe("Home");
    }

    [Fact]
    public async Task Moving_a_tag_row_to_another_page_updates_both_pages()
    {
        var (from, to) = (await AddPageAsync(), await AddPageAsync());
        var work = await AddTagAsync("Work");
        await TagAsync(from, work);

        await using (var db = CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE page_tags SET page_id = {to} WHERE page_id = {from}");
        }

        (await TagTextAsync(from)).ShouldBe("");
        (await TagTextAsync(to)).ShouldBe("Work");
    }

    [Fact]
    public async Task Deleting_a_page_with_tags_is_not_held_up_by_the_trigger()
    {
        var page = await AddPageAsync();
        await TagAsync(page, await AddTagAsync("Work"));

        await using var db = CreateContext();
        await db.Pages.Where(p => p.Id == page).ExecuteDeleteAsync();

        (await db.Pages.CountAsync()).ShouldBe(0);
        (await db.PageTags.CountAsync()).ShouldBe(0);
    }
}
