using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Links;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Links;

/// <summary>
/// The backlinks of a page over REST (run B.1): from the link index, only pages that are live and that the person may see.
/// The index is written straight into the table: what puts rows there is tested with the compactor.
/// </summary>
public class BacklinkEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _owner = await _factory.SignedInClientAsync();
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private async Task<PageDto> CreateAsync(string title, Guid? parentId = null)
    {
        var response = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title, parentId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PageChange>())!.Page;
    }

    private Task LinkAsync(Guid source, Guid target) => UseDbAsync(async db =>
    {
        db.PageLinks.Add(new PageLink { SourcePageId = source, TargetPageId = target });
        await db.SaveChangesAsync();
        return 0;
    });

    private async Task<BacklinksDto> BacklinksAsync(Guid pageId, HttpClient? client = null)
    {
        var response = await (client ?? _owner).GetAsync($"/api/pages/{pageId}/backlinks");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BacklinksDto>())!;
    }

    [Fact]
    public async Task It_lists_the_pages_that_link_to_the_page_by_title_with_the_title_of_their_parent()
    {
        var target = await CreateAsync("Target");
        var notebook = await CreateAsync("Notebook");
        var banana = await CreateAsync("banana", notebook.Id);
        var apple = await CreateAsync("Apple");
        var cherry = await CreateAsync("Cherry", notebook.Id);
        foreach (var source in new[] { banana, apple, cherry })
        {
            await LinkAsync(source.Id, target.Id);
        }

        var backlinks = await BacklinksAsync(target.Id);

        backlinks.More.ShouldBeFalse();
        backlinks.Items.ShouldBe(
        [
            new ListedPage(apple.Id, "Apple", null),
            new ListedPage(banana.Id, "banana", "Notebook"),
            new ListedPage(cherry.Id, "Cherry", "Notebook"),
        ]);
    }

    [Fact]
    public async Task A_page_nothing_links_to_has_none_and_a_page_is_never_its_own_backlink()
    {
        var alone = await CreateAsync("Alone");
        var self = await CreateAsync("Self");
        await LinkAsync(self.Id, self.Id);

        var none = await BacklinksAsync(alone.Id);
        none.Items.ShouldBeEmpty();
        none.More.ShouldBeFalse();
        (await BacklinksAsync(self.Id)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_in_the_trash_and_a_page_below_one_are_not_listed_and_come_back_when_restored()
    {
        var target = await CreateAsync("Target");
        var trashed = await CreateAsync("Trashed");
        var parent = await CreateAsync("Parent");
        var below = await CreateAsync("Below", parent.Id);
        var live = await CreateAsync("Live");
        foreach (var source in new[] { trashed, below, live })
        {
            await LinkAsync(source.Id, target.Id);
        }

        (await _owner.PostJsonAsync($"/api/pages/{trashed.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _owner.PostJsonAsync($"/api/pages/{parent.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await BacklinksAsync(target.Id)).Items.Select(i => i.Title).ShouldBe(["Live"]);

        (await _owner.PostJsonAsync($"/api/pages/{parent.Id}/restore")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await BacklinksAsync(target.Id)).Items.Select(i => i.Title).ShouldBe(["Below", "Live"]);
    }

    [Fact]
    public async Task A_link_to_a_page_that_is_in_the_trash_is_still_listed_on_that_page_for_when_it_is_restored()
    {
        var target = await CreateAsync("Target");
        var source = await CreateAsync("Source");
        await LinkAsync(source.Id, target.Id);
        (await _owner.PostJsonAsync($"/api/pages/{target.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The trashed page itself is still readable (the trash view opens it), and so are its backlinks.
        (await BacklinksAsync(target.Id)).Items.Select(i => i.Title).ShouldBe(["Source"]);
    }

    [Fact]
    public async Task A_page_of_a_workspace_the_person_is_not_in_is_not_listed_and_one_of_a_workspace_they_can_view_is()
    {
        var target = await CreateAsync("Target");
        var (hidden, shared) = await UseDbAsync(async db =>
        {
            var owner = await db.Users.Select(u => u.Id).FirstAsync();
            var other = new User { Email = "other@example.com", DisplayName = "Other", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(other);
            var secret = new Workspace { Name = "Secret", OwnerId = other.Id, CreatedAt = DateTimeOffset.UtcNow };
            var visible = new Workspace { Name = "Visible", OwnerId = other.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.Workspaces.AddRange(secret, visible);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = visible.Id, UserId = owner, Role = WorkspaceRole.Viewer, CreatedAt = DateTimeOffset.UtcNow });
            Page Make(Workspace ws, string title) => new() { WorkspaceId = ws.Id, Title = title, SortKey = "a0", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var hiddenPage = Make(secret, "Hidden page");
            var sharedPage = Make(visible, "Shared page");
            db.Pages.AddRange(hiddenPage, sharedPage);
            await db.SaveChangesAsync();
            return (hiddenPage.Id, sharedPage.Id);
        });
        await LinkAsync(hidden, target.Id);
        await LinkAsync(shared, target.Id);

        (await BacklinksAsync(target.Id)).Items.Select(i => i.Title).ShouldBe(["Shared page"]);
    }

    [Fact]
    public async Task At_most_two_hundred_are_listed_and_the_answer_says_there_are_more()
    {
        var target = await CreateAsync("Target");
        var sources = await UseDbAsync(async db =>
        {
            var pages = Enumerable.Range(0, BacklinkEndpoints.MaxBacklinks + 1)
                .Select(i => new Page { WorkspaceId = _workspaceId, Title = $"Source {i:D3}", SortKey = $"a{i:D4}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow })
                .ToList();
            db.Pages.AddRange(pages);
            await db.SaveChangesAsync();
            db.PageLinks.AddRange(pages.Select(p => new PageLink { SourcePageId = p.Id, TargetPageId = target.Id }));
            await db.SaveChangesAsync();
            return pages;
        });

        var backlinks = await BacklinksAsync(target.Id);

        backlinks.Items.Count.ShouldBe(BacklinkEndpoints.MaxBacklinks);
        backlinks.More.ShouldBeTrue();
        backlinks.Items[0].Title.ShouldBe("Source 000");
        sources.Count.ShouldBe(BacklinkEndpoints.MaxBacklinks + 1);
    }

    [Fact]
    public async Task A_stranger_gets_not_found_and_an_anonymous_client_is_refused()
    {
        var target = await CreateAsync("Secret target");
        var source = await CreateAsync("Secret source");
        await LinkAsync(source.Id, target.Id);
        await UseDbAsync(async db =>
        {
            db.Users.Add(new User { Email = "stranger@example.com", DisplayName = "S", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        var stranger = _factory.CreateClient();
        (await stranger.LoginAsync("stranger@example.com")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var refused = await stranger.GetAsync($"/api/pages/{target.Id}/backlinks");
        var missing = await stranger.GetAsync($"/api/pages/{Guid.NewGuid()}/backlinks");
        var anonymous = await _factory.CreateClient().GetAsync($"/api/pages/{target.Id}/backlinks");

        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsStringAsync()).ShouldBe("{\"code\":\"not_found\"}");
        (await missing.Content.ReadAsStringAsync()).ShouldBe("{\"code\":\"not_found\"}");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
