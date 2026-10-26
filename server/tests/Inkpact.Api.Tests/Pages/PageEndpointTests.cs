using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Pages;

[Collection(PostgresCollection.Name)]
public class PageEndpointTests(PostgresFixture postgres) : IAsyncLifetime
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

    /// <summary>A second person with the given role in the workspace, signed in on their own client.</summary>
    private async Task<HttpClient> SignedInAsAsync(WorkspaceRole role)
    {
        var email = $"{role}-{Guid.NewGuid():N}@example.com".ToLowerInvariant();
        await UseDbAsync(async db =>
        {
            var user = new User { Email = email, DisplayName = role.ToString(), PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = role, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        var client = _factory.CreateClient();
        (await client.LoginAsync(email)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>())!;

    private async Task<PageDto> CreateAsync(HttpClient client, string title, Guid? parentId = null)
    {
        var response = await client.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title, parentId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await ReadAsync<PageChange>(response)).Page;
    }

    // ---- who may do what ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_can_create_read_rename_move_trash_and_restore()
    {
        var a = await CreateAsync(_owner, "A");
        var b = await CreateAsync(_owner, "B");

        (await _owner.PatchJsonAsync($"/api/pages/{a.Id}", new { title = "A renamed" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _owner.PostJsonAsync($"/api/pages/{a.Id}/move", new { parentId = b.Id })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _owner.PostJsonAsync($"/api/pages/{b.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _owner.PostJsonAsync($"/api/pages/{b.Id}/restore")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var tree = await ReadAsync<TreeDto>(await _owner.GetAsync($"/api/workspaces/{_workspaceId}/pages"));
        tree.Pages.Select(p => p.Title).ShouldBe(["B", "A renamed"]);
        tree.Pages[1].ParentId.ShouldBe(b.Id);
    }

    [Fact]
    public async Task A_viewer_can_read_but_not_change_anything()
    {
        var page = await CreateAsync(_owner, "A");
        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);

        (await viewer.GetAsync($"/api/workspaces/{_workspaceId}/pages")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/workspaces/{_workspaceId}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/pages/{page.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = new[]
        {
            await viewer.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "x" }),
            await viewer.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "x" }),
            await viewer.PostJsonAsync($"/api/pages/{page.Id}/move", new { }),
            await viewer.PostJsonAsync($"/api/pages/{page.Id}/trash"),
            await viewer.PostJsonAsync($"/api/pages/{page.Id}/restore"),
        };
        foreach (var response in refused)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await response.ReadErrorAsync()).ShouldBe(new ApiError("forbidden"));
        }

        (await UseDbAsync(db => db.Pages.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task An_editor_can_change_the_tree()
    {
        var page = await CreateAsync(_owner, "A");
        var editor = await SignedInAsAsync(WorkspaceRole.Editor);

        (await CreateAsync(editor, "By the editor")).Title.ShouldBe("By the editor");
        (await editor.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "Renamed" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await editor.PostJsonAsync($"/api/pages/{page.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_stranger_gets_not_found_for_everything_and_learns_nothing()
    {
        var page = await CreateAsync(_owner, "Secret title");
        var stranger = _factory.CreateClient();
        await UseDbAsync(async db =>
        {
            db.Users.Add(new User { Email = "stranger@example.com", DisplayName = "S", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        (await stranger.LoginAsync("stranger@example.com")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var responses = new[]
        {
            await stranger.GetAsync($"/api/workspaces/{_workspaceId}/pages"),
            await stranger.GetAsync($"/api/workspaces/{_workspaceId}/trash"),
            await stranger.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "x" }),
            await stranger.GetAsync($"/api/pages/{page.Id}"),
            await stranger.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "x" }),
            await stranger.PostJsonAsync($"/api/pages/{page.Id}/move", new { }),
            await stranger.PostJsonAsync($"/api/pages/{page.Id}/trash"),
            await stranger.PostJsonAsync($"/api/pages/{page.Id}/restore"),
            await stranger.GetAsync($"/api/pages/{Guid.NewGuid()}"), // one that does not exist looks the same
        };

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).ShouldBe("{\"code\":\"not_found\"}");
        }
    }

    [Fact]
    public async Task Nobody_signed_out_can_do_anything()
    {
        var page = await CreateAsync(_owner, "A");
        var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync($"/api/workspaces/{_workspaceId}/pages")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/pages/{page.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostJsonAsync($"/api/pages/{page.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_change_needs_the_csrf_token_and_json()
    {
        var page = await CreateAsync(_owner, "A");

        var noToken = await _owner.PostJsonWithoutTokenAsync($"/api/pages/{page.Id}/trash");
        var form = await _owner.PostAsync($"/api/pages/{page.Id}/trash", new FormUrlEncodedContent(new Dictionary<string, string>()));

        noToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noToken.ReadErrorAsync()).Code.ShouldBe("csrf_invalid");
        form.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await RowAsync(page.Id)).DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_page_of_another_workspace_cannot_be_used_as_a_parent()
    {
        var other = await UseDbAsync(async db =>
        {
            var user = new User { Email = "other@example.com", DisplayName = "O", CreatedAt = DateTimeOffset.UtcNow };
            var workspace = new Workspace { Name = "Other", OwnerId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
            var foreign = new Page { WorkspaceId = workspace.Id, Title = "Foreign", SortKey = "a0", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.AddRange(user, workspace, foreign);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return foreign.Id;
        });

        var response = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "x", parentId = other });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("parent_invalid", "parentId"));
    }

    // ---- what comes back ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_answers_201_with_the_page_and_no_conflict()
    {
        var response = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "A" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var change = await ReadAsync<PageChange>(response);
        change.Page.Title.ShouldBe("A");
        change.Page.Version.ShouldBe(1);
        change.Conflict.ShouldBeFalse();
        change.RestoredTo.ShouldBeNull();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("restoredTo");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("deletedAt");
    }

    [Fact]
    public async Task The_error_codes_come_back_as_json_with_the_right_status()
    {
        var a = await CreateAsync(_owner, "A");
        var b = await CreateAsync(_owner, "B", parentId: a.Id);

        var cycle = await _owner.PostJsonAsync($"/api/pages/{a.Id}/move", new { parentId = b.Id });
        var position = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "x", afterId = Guid.NewGuid() });
        var title = await _owner.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = new string('x', 501) });
        await _owner.PostJsonAsync($"/api/pages/{a.Id}/trash");
        var inTrash = await _owner.PatchJsonAsync($"/api/pages/{a.Id}", new { title = "x" });
        var hidden = await _owner.PostJsonAsync($"/api/pages/{b.Id}/restore");

        (cycle.StatusCode, (await cycle.ReadErrorAsync())).ShouldBe((HttpStatusCode.Conflict, new ApiError("move_cycle", "parentId")));
        (position.StatusCode, (await position.ReadErrorAsync())).ShouldBe((HttpStatusCode.BadRequest, new ApiError("position_invalid", "position")));
        (title.StatusCode, (await title.ReadErrorAsync())).ShouldBe((HttpStatusCode.BadRequest, new ApiError("title_invalid", "title")));
        (inTrash.StatusCode, (await inTrash.ReadErrorAsync())).ShouldBe((HttpStatusCode.Conflict, new ApiError("page_in_trash")));
        (hidden.StatusCode, (await hidden.ReadErrorAsync())).ShouldBe((HttpStatusCode.Conflict, new ApiError("restore_the_parent")));
    }

    [Fact]
    public async Task A_stale_version_is_applied_and_flagged()
    {
        var page = await CreateAsync(_owner, "A");
        await _owner.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "B", expectedVersion = 1 });

        var late = await ReadAsync<PageChange>(await _owner.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "C", expectedVersion = 1 }));

        late.Conflict.ShouldBeTrue();
        late.Page.Title.ShouldBe("C");
        late.Page.Version.ShouldBe(3);
    }

    [Fact]
    public async Task Restore_says_where_the_page_went()
    {
        var parent = await CreateAsync(_owner, "Parent");
        var child = await CreateAsync(_owner, "Child", parent.Id);
        await _owner.PostJsonAsync($"/api/pages/{child.Id}/trash");
        await _owner.PostJsonAsync($"/api/pages/{parent.Id}/trash");

        var change = await ReadAsync<PageChange>(await _owner.PostJsonAsync($"/api/pages/{child.Id}/restore"));

        change.RestoredTo.ShouldBe("root");
        change.Page.ParentId.ShouldBeNull();
    }

    [Fact]
    public async Task One_page_is_read_with_whether_it_is_in_the_trash()
    {
        var parent = await CreateAsync(_owner, "Parent");
        var child = await CreateAsync(_owner, "Child", parent.Id);
        await _owner.PostJsonAsync($"/api/pages/{parent.Id}/trash");

        var detail = await ReadAsync<PageDetail>(await _owner.GetAsync($"/api/pages/{child.Id}"));

        detail.InTrash.ShouldBeTrue();
        detail.Page.Title.ShouldBe("Child");
    }

    [Fact]
    public async Task The_tree_and_the_trash_are_read_as_json()
    {
        var a = await CreateAsync(_owner, "A");
        var b = await CreateAsync(_owner, "B");
        await _owner.PostJsonAsync($"/api/pages/{b.Id}/trash");

        var tree = await ReadAsync<TreeDto>(await _owner.GetAsync($"/api/workspaces/{_workspaceId}/pages"));
        var trash = await ReadAsync<List<PageDto>>(await _owner.GetAsync($"/api/workspaces/{_workspaceId}/trash"));

        tree.Pages.Select(p => p.Id).ShouldBe([a.Id]);
        tree.TreeVersion.ShouldBeGreaterThan(0);
        trash.Select(p => p.Id).ShouldBe([b.Id]);
        trash.Single().DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Malformed_ids_in_the_address_are_not_found()
    {
        (await _owner.GetAsync("/api/pages/not-a-guid")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _owner.GetAsync("/api/workspaces/not-a-guid/pages")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _owner.PostJsonAsync("/api/pages/not-a-guid/trash")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_body_that_is_not_json_for_the_endpoint_is_a_bad_request_not_a_crash()
    {
        var page = await CreateAsync(_owner, "A");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/pages/{page.Id}/move") { Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(CsrfProtection.HeaderName, (await _owner.GetStateAsync()).CsrfToken);

        var response = await _owner.SendAsync(request);

        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    private async Task<Page> RowAsync(Guid id) => await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Id == id));
}
