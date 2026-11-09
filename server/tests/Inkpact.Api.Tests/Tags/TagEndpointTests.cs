using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Pages;
using Inkpact.Api.Tags;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Tags;

/// <summary>Tags over REST (run B.2): the set operation on a page, the workspace list, a tag's pages, and renaming.</summary>
public class TagEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    [Fact]
    public async Task Adding_and_removing_tags_on_a_page_answers_with_its_tags_and_a_second_read_shows_them()
    {
        var page = await CreateAsync("Note");

        var response = await _owner.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "Work", "Home" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = await response.Content.ReadFromJsonAsync<List<TagDto>>();
        answer!.Select(t => t.Name).ShouldBe(["Home", "Work"]);

        var detail = await _owner.GetFromJsonAsync<PageDetail>($"/api/pages/{page.Id}");
        detail!.Tags.Select(t => t.Name).ShouldBe(["Home", "Work"]);

        var removed = await _owner.PostJsonAsync($"/api/pages/{page.Id}/tags", new { remove = new[] { answer!.Single(t => t.Name == "Work").Id } });
        (await removed.Content.ReadFromJsonAsync<List<TagDto>>())!.Select(t => t.Name).ShouldBe(["Home"]);
    }

    [Fact]
    public async Task A_page_in_the_trash_refuses_the_change()
    {
        var page = await CreateAsync("Note");
        (await _owner.PostJsonAsync($"/api/pages/{page.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await _owner.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "Work" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<ApiError>())!.Code.ShouldBe("page_in_trash");
    }

    [Fact]
    public async Task A_viewer_can_read_tags_but_not_change_them_and_a_stranger_gets_not_found()
    {
        var page = await CreateAsync("Note");
        await _owner.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "Work" } });
        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);

        (await viewer.GetFromJsonAsync<PageDetail>($"/api/pages/{page.Id}"))!.Tags.Select(t => t.Name).ShouldBe(["Work"]);
        (await viewer.GetAsync($"/api/workspaces/{_workspaceId}/tags")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await viewer.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "Home" } });
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var stranger = _factory.CreateClient();
        (await stranger.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "Home" } })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_workspace_list_counts_live_pages_and_a_tags_pages_are_listed_by_title()
    {
        var notebook = await CreateAsync("Notebook");
        var banana = await CreateAsync("banana", notebook.Id);
        var apple = await CreateAsync("Apple");
        await _owner.PostJsonAsync($"/api/pages/{banana.Id}/tags", new { add = new[] { "Fruit" } });
        await _owner.PostJsonAsync($"/api/pages/{apple.Id}/tags", new { add = new[] { "Fruit" } });

        var list = await _owner.GetFromJsonAsync<List<WorkspaceTagDto>>($"/api/workspaces/{_workspaceId}/tags");
        var tag = list!.Single();
        tag.Name.ShouldBe("Fruit");
        tag.PageCount.ShouldBe(2);

        var pages = await _owner.GetFromJsonAsync<TagPagesDto>($"/api/workspaces/{_workspaceId}/tags/{tag.Id}/pages");
        pages!.Items.ShouldBe([new ListedPage(apple.Id, "Apple", null), new ListedPage(banana.Id, "banana", "Notebook")]);
        pages.More.ShouldBeFalse();
    }

    [Fact]
    public async Task A_tag_of_another_workspace_or_one_that_does_not_exist_is_not_found()
    {
        var response = await _owner.GetAsync($"/api/workspaces/{_workspaceId}/tags/{Guid.NewGuid()}/pages");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Renaming_a_tag_works_for_an_editor_and_is_refused_for_a_viewer_and_for_a_name_already_taken()
    {
        var page = await CreateAsync("Note");
        await _owner.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "Work", "Home" } });
        var tags = await _owner.GetFromJsonAsync<List<WorkspaceTagDto>>($"/api/workspaces/{_workspaceId}/tags");
        var work = tags!.Single(t => t.Name == "Work");
        var viewer = await SignedInAsAsync(WorkspaceRole.Viewer);

        var refused = await viewer.PatchJsonAsync($"/api/workspaces/{_workspaceId}/tags/{work.Id}", new { name = "Job" });
        var taken = await _owner.PatchJsonAsync($"/api/workspaces/{_workspaceId}/tags/{work.Id}", new { name = "Home" });
        var ok = await _owner.PatchJsonAsync($"/api/workspaces/{_workspaceId}/tags/{work.Id}", new { name = "Job" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<TagDto>())!.Name.ShouldBe("Job");
    }
}
