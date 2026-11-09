using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Favorites;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Favorites;

/// <summary>Favorites and recently opened pages over REST (run B.3): private to the person, live pages only.</summary>
public class FavoriteEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly MutableTimeProvider _time = new(DateTimeOffset.UtcNow);
    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: _time);
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        // A JSON body like the web client's: the CSRF check reads a request with no content type as a form.
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) };
        request.Headers.Add(CsrfProtection.HeaderName, (await client.GetStateAsync()).CsrfToken);
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> StarAsync(Guid pageId, HttpClient? client = null) => SendAsync(client ?? _owner, HttpMethod.Put, $"/api/pages/{pageId}/favorite");

    private Task<HttpResponseMessage> UnstarAsync(Guid pageId, HttpClient? client = null) => SendAsync(client ?? _owner, HttpMethod.Delete, $"/api/pages/{pageId}/favorite");

    private async Task<Guid[]> FavoritesAsync(HttpClient? client = null, Guid? workspaceId = null)
    {
        var response = await (client ?? _owner).GetAsync($"/api/workspaces/{workspaceId ?? _workspaceId}/favorites");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Guid[]>())!;
    }

    private async Task VisitAsync(Guid pageId, HttpClient? client = null)
    {
        (await (client ?? _owner).PostJsonAsync($"/api/pages/{pageId}/visit")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _time.Advance(TimeSpan.FromMinutes(1));
    }

    private async Task<RecentPageDto[]> RecentAsync(HttpClient? client = null)
    {
        var response = await (client ?? _owner).GetAsync($"/api/workspaces/{_workspaceId}/recent");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<RecentPageDto[]>())!;
    }

    /// <summary>A second person who is a member of the workspace with this role, signed in.</summary>
    private async Task<HttpClient> MemberAsync(WorkspaceRole role, string email = "member@example.com")
    {
        await UseDbAsync(async db =>
        {
            var user = new User { Email = email, DisplayName = "Member", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = role, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        var client = _factory.CreateClient();
        (await client.LoginAsync(email)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return client;
    }

    // ---- favorites -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Starring_and_unstarring_are_idempotent_and_the_list_keeps_the_order_they_were_starred_in()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        foreach (var page in new[] { c, a, b, a })
        {
            (await StarAsync(page.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        (await FavoritesAsync()).ShouldBe([c.Id, a.Id, b.Id]);

        (await UnstarAsync(a.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UnstarAsync(a.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FavoritesAsync()).ShouldBe([c.Id, b.Id]);
    }

    [Fact]
    public async Task A_viewer_may_star_a_page()
    {
        var page = await CreateAsync("Read only");
        var viewer = await MemberAsync(WorkspaceRole.Viewer);

        (await StarAsync(page.Id, viewer)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await FavoritesAsync(viewer)).ShouldBe([page.Id]);
    }

    [Fact]
    public async Task A_page_in_the_trash_cannot_be_starred_but_can_be_unstarred_and_a_favorite_is_hidden_until_restored()
    {
        var page = await CreateAsync("Doomed");
        var other = await CreateAsync("Other");
        await StarAsync(page.Id);
        await StarAsync(other.Id);

        (await _owner.PostJsonAsync($"/api/pages/{page.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FavoritesAsync()).ShouldBe([other.Id]);
        var refused = await StarAsync(page.Id);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadErrorAsync()).Code.ShouldBe("page_in_trash");

        (await _owner.PostJsonAsync($"/api/pages/{page.Id}/restore")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FavoritesAsync()).ShouldBe([page.Id, other.Id]);

        (await _owner.PostJsonAsync($"/api/pages/{page.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UnstarAsync(page.Id)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _owner.PostJsonAsync($"/api/pages/{page.Id}/restore")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FavoritesAsync()).ShouldBe([other.Id]);
    }

    [Fact]
    public async Task A_page_below_a_page_in_the_trash_is_hidden_too()
    {
        var parent = await CreateAsync("Parent");
        var child = await CreateAsync("Child", parent.Id);
        await StarAsync(child.Id);

        (await _owner.PostJsonAsync($"/api/pages/{parent.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FavoritesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task At_most_two_hundred_pages_can_be_starred_and_starring_one_again_still_works()
    {
        var ids = await UseDbAsync(async db =>
        {
            var pages = Enumerable.Range(0, FavoriteEndpoints.MaxFavorites + 1)
                .Select(i => new Page { WorkspaceId = _workspaceId, Title = $"P{i}", SortKey = $"a{i:D4}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow })
                .ToList();
            db.Pages.AddRange(pages);
            await db.SaveChangesAsync();
            var user = await db.Users.Select(u => u.Id).SingleAsync();
            db.Favorites.AddRange(pages.Take(FavoriteEndpoints.MaxFavorites).Select(p => new Favorite { UserId = user, PageId = p.Id, CreatedAt = DateTimeOffset.UtcNow }));
            await db.SaveChangesAsync();
            return pages.Select(p => p.Id).ToList();
        });

        (await StarAsync(ids[0])).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var refused = await StarAsync(ids[^1]);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadErrorAsync()).Code.ShouldBe("too_many_favorites");
    }

    [Fact]
    public async Task Parallel_stars_of_different_pages_never_pass_the_limit()
    {
        var ids = await UseDbAsync(async db =>
        {
            var pages = Enumerable.Range(0, FavoriteEndpoints.MaxFavorites + 5)
                .Select(i => new Page { WorkspaceId = _workspaceId, Title = $"P{i}", SortKey = $"a{i:D4}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow })
                .ToList();
            db.Pages.AddRange(pages);
            await db.SaveChangesAsync();
            var user = await db.Users.Select(u => u.Id).SingleAsync();
            db.Favorites.AddRange(pages.Take(FavoriteEndpoints.MaxFavorites - 1).Select(p => new Favorite { UserId = user, PageId = p.Id, CreatedAt = DateTimeOffset.UtcNow }));
            await db.SaveChangesAsync();
            return pages.Skip(FavoriteEndpoints.MaxFavorites - 1).Select(p => p.Id).ToList();
        });

        var responses = await Task.WhenAll(ids.Select(id => StarAsync(id)));

        responses.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(ids.Count - 1);
        (await UseDbAsync(db => db.Favorites.CountAsync())).ShouldBe(FavoriteEndpoints.MaxFavorites);
    }

    [Fact]
    public async Task Favorites_are_private_to_the_person()
    {
        var page = await CreateAsync("Mine");
        var editor = await MemberAsync(WorkspaceRole.Editor);
        await StarAsync(page.Id);

        (await FavoritesAsync(editor)).ShouldBeEmpty();
        (await UnstarAsync(page.Id, editor)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FavoritesAsync()).ShouldBe([page.Id]);
    }

    [Fact]
    public async Task A_page_that_is_not_in_the_workspace_asked_about_is_not_listed_and_a_stranger_gets_not_found()
    {
        var page = await CreateAsync("Mine");
        await StarAsync(page.Id);
        var stranger = _factory.CreateClient();
        await UseDbAsync(async db =>
        {
            db.Users.Add(new User { Email = "stranger@example.com", DisplayName = "S", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        (await stranger.LoginAsync("stranger@example.com")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        foreach (var response in new[]
        {
            await StarAsync(page.Id, stranger),
            await UnstarAsync(page.Id, stranger),
            await stranger.PostJsonAsync($"/api/pages/{page.Id}/visit"),
            await StarAsync(Guid.NewGuid()),
            await stranger.GetAsync($"/api/workspaces/{_workspaceId}/favorites"),
            await stranger.GetAsync($"/api/workspaces/{_workspaceId}/recent"),
        })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.ReadErrorAsync()).Code.ShouldBe("not_found");
        }

        (await FavoritesAsync()).ShouldBe([page.Id]);
    }

    [Fact]
    public async Task Anonymous_requests_to_all_five_endpoints_are_refused()
    {
        var anonymous = _factory.CreateClient();
        var id = Guid.NewGuid();

        (await anonymous.GetAsync($"/api/workspaces/{id}/favorites")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/workspaces/{id}/recent")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(anonymous, HttpMethod.Put, $"/api/pages/{id}/favorite")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(anonymous, HttpMethod.Delete, $"/api/pages/{id}/favorite")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostJsonAsync($"/api/pages/{id}/visit")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_page_deleted_for_good_takes_its_favorite_and_visit_rows_with_it()
    {
        var page = await CreateAsync("Gone");
        await StarAsync(page.Id);
        await VisitAsync(page.Id);

        var left = await UseDbAsync(async db =>
        {
            await db.Pages.Where(p => p.Id == page.Id).ExecuteDeleteAsync();
            return (await db.Favorites.CountAsync(), await db.PageVisits.CountAsync());
        });

        left.ShouldBe((0, 0));
        (await FavoritesAsync()).ShouldBeEmpty();
        (await RecentAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Starring_and_visiting_change_neither_the_page_nor_the_tree_version()
    {
        var page = await CreateAsync("Quiet");
        var before = await UseDbAsync(async db => (await db.Pages.AsNoTracking().SingleAsync(p => p.Id == page.Id), await db.Workspaces.Select(w => w.TreeVersion).SingleAsync(), await db.AuditEvents.CountAsync()));

        await StarAsync(page.Id);
        await VisitAsync(page.Id);

        var after = await UseDbAsync(async db => (await db.Pages.AsNoTracking().SingleAsync(p => p.Id == page.Id), await db.Workspaces.Select(w => w.TreeVersion).SingleAsync(), await db.AuditEvents.CountAsync()));
        after.Item1.Version.ShouldBe(before.Item1.Version);
        after.Item1.UpdatedAt.ShouldBe(before.Item1.UpdatedAt);
        after.Item2.ShouldBe(before.Item2);
        after.Item3.ShouldBe(before.Item3);
    }

    // ---- visits --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_open_sets_the_time_and_a_second_open_moves_the_page_to_the_front()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var firstOpen = _time.GetUtcNow();

        await VisitAsync(a.Id);
        await VisitAsync(b.Id);
        (await RecentAsync()).Select(r => r.PageId).ShouldBe([b.Id, a.Id]);

        await VisitAsync(a.Id);
        var recent = await RecentAsync();
        recent.Select(r => r.PageId).ShouldBe([a.Id, b.Id]);
        recent[0].OpenedAt.ShouldBeGreaterThan(firstOpen);
        recent.Length.ShouldBe(2);
    }

    [Fact]
    public async Task The_fifty_first_page_drops_the_oldest()
    {
        var ids = await UseDbAsync(async db =>
        {
            var pages = Enumerable.Range(0, FavoriteEndpoints.MaxVisits + 1)
                .Select(i => new Page { WorkspaceId = _workspaceId, Title = $"P{i}", SortKey = $"a{i:D4}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow })
                .ToList();
            db.Pages.AddRange(pages);
            await db.SaveChangesAsync();
            return pages.Select(p => p.Id).ToList();
        });

        foreach (var id in ids)
        {
            await VisitAsync(id);
        }

        var recent = await RecentAsync();
        recent.Length.ShouldBe(FavoriteEndpoints.MaxVisits);
        recent[0].PageId.ShouldBe(ids[^1]);
        recent.Select(r => r.PageId).ShouldNotContain(ids[0]);
        (await UseDbAsync(db => db.PageVisits.CountAsync())).ShouldBe(FavoriteEndpoints.MaxVisits);
    }

    [Fact]
    public async Task Recent_pages_are_live_pages_only_and_come_back_with_a_restore()
    {
        var kept = await CreateAsync("Kept");
        var trashed = await CreateAsync("Trashed");
        await VisitAsync(trashed.Id);
        await VisitAsync(kept.Id);

        (await _owner.PostJsonAsync($"/api/pages/{trashed.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RecentAsync()).Select(r => r.PageId).ShouldBe([kept.Id]);

        (await _owner.PostJsonAsync($"/api/pages/{trashed.Id}/restore")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RecentAsync()).Select(r => r.PageId).ShouldBe([kept.Id, trashed.Id]);
    }

    [Fact]
    public async Task Visits_are_private_to_the_person()
    {
        var page = await CreateAsync("Mine");
        var viewer = await MemberAsync(WorkspaceRole.Viewer);
        await VisitAsync(page.Id);

        (await RecentAsync(viewer)).ShouldBeEmpty();

        await VisitAsync(page.Id, viewer);
        (await RecentAsync(viewer)).Select(r => r.PageId).ShouldBe([page.Id]);
        (await UseDbAsync(db => db.PageVisits.CountAsync())).ShouldBe(2);
    }

    [Fact]
    public async Task Parallel_opens_of_different_pages_with_a_full_list_all_succeed_and_keep_fifty()
    {
        var ids = await UseDbAsync(async db =>
        {
            var pages = Enumerable.Range(0, FavoriteEndpoints.MaxVisits + 10)
                .Select(i => new Page { WorkspaceId = _workspaceId, Title = $"P{i}", SortKey = $"a{i:D4}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow })
                .ToList();
            db.Pages.AddRange(pages);
            await db.SaveChangesAsync();
            var user = await db.Users.Select(u => u.Id).SingleAsync();
            db.PageVisits.AddRange(pages.Take(FavoriteEndpoints.MaxVisits).Select((p, i) => new PageVisit { UserId = user, PageId = p.Id, OpenedAt = DateTimeOffset.UtcNow.AddMinutes(-100 + i) }));
            await db.SaveChangesAsync();
            return pages.Skip(FavoriteEndpoints.MaxVisits).Select(p => p.Id).ToList();
        });

        var responses = await Task.WhenAll(ids.Select(id => _owner.PostJsonAsync($"/api/pages/{id}/visit")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
        (await UseDbAsync(db => db.PageVisits.CountAsync())).ShouldBe(FavoriteEndpoints.MaxVisits);
        (await RecentAsync()).Select(r => r.PageId).Take(ids.Count).ShouldBe(ids, ignoreOrder: true);
    }

    [Fact]
    public async Task Parallel_opens_of_one_page_by_one_person_end_with_one_row()
    {
        var page = await CreateAsync("Busy");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _owner.PostJsonAsync($"/api/pages/{page.Id}/visit")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
        (await UseDbAsync(db => db.PageVisits.CountAsync())).ShouldBe(1);
    }
}
