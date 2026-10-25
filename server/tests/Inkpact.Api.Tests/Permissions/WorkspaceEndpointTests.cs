using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Permissions;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Permissions;

[Collection(PostgresCollection.Name)]
public class WorkspaceEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _admin = await _factory.SignedInClientAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private async Task<Guid> AdminIdAsync() => await UseDbAsync(db => db.Users.Where(u => u.NormalizedEmail == AuthTestExtensions.Email).Select(u => u.Id).SingleAsync());

    private async Task<Guid> OwnWorkspaceIdAsync() => await UseDbAsync(async db => (await db.Workspaces.Where(w => w.Name == "Private").Select(w => w.Id).ToListAsync()).Single());

    /// <summary>A workspace of somebody else, with the given role for the signed-in administrator, or none.</summary>
    private async Task<Guid> AddWorkspaceOfSomeoneElseAsync(string name, WorkspaceRole? roleForAdmin)
    {
        var adminId = await AdminIdAsync();
        return await UseDbAsync(async db =>
        {
            var other = new User { Email = $"{Guid.NewGuid():N}@example.com", DisplayName = "Other", CreatedAt = DateTimeOffset.UtcNow };
            var workspace = new Workspace { Name = name, OwnerId = other.Id, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(other, workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = other.Id, Role = WorkspaceRole.Owner, CreatedAt = DateTimeOffset.UtcNow });
            if (roleForAdmin is { } role)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = adminId, Role = role, CreatedAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync();
            return workspace.Id;
        });
    }

    [Fact]
    public async Task Setup_makes_the_administrator_the_owner_of_their_workspace()
    {
        var member = await UseDbAsync(db => db.WorkspaceMembers.SingleAsync());

        member.UserId.ShouldBe(await AdminIdAsync());
        member.WorkspaceId.ShouldBe(await OwnWorkspaceIdAsync());
        member.Role.ShouldBe(WorkspaceRole.Owner);
    }

    [Fact]
    public async Task The_list_has_only_my_workspaces_with_my_role_in_each()
    {
        var shared = await AddWorkspaceOfSomeoneElseAsync("Shared with me", WorkspaceRole.Viewer);
        await AddWorkspaceOfSomeoneElseAsync("Not mine", roleForAdmin: null);

        var list = (await _admin.GetFromJsonAsync<List<WorkspaceSummary>>("/api/workspaces"))!;

        list.Select(w => (w.Name, w.Role)).ShouldBe([("Private", "owner"), ("Shared with me", "viewer")]);
        list.Select(w => w.Id).ShouldContain(shared);
    }

    [Fact]
    public async Task The_list_needs_a_signed_in_user()
    {
        var response = await _factory.CreateClient().GetAsync("/api/workspaces");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task My_own_workspace_can_be_read()
    {
        var id = await OwnWorkspaceIdAsync();

        var workspace = (await _admin.GetFromJsonAsync<WorkspaceSummary>($"/api/workspaces/{id}"))!;

        workspace.ShouldBe(new WorkspaceSummary(id, "Private", "owner"));
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer, "viewer")]
    [InlineData(WorkspaceRole.Editor, "editor")]
    public async Task A_workspace_I_was_given_a_role_in_can_be_read_and_shows_that_role(WorkspaceRole given, string expected)
    {
        var id = await AddWorkspaceOfSomeoneElseAsync("Theirs", given);

        var workspace = (await _admin.GetFromJsonAsync<WorkspaceSummary>($"/api/workspaces/{id}"))!;

        workspace.Role.ShouldBe(expected);
    }

    [Fact]
    public async Task Someone_elses_workspace_is_not_found_not_forbidden()
    {
        var id = await AddWorkspaceOfSomeoneElseAsync("Secret", roleForAdmin: null);

        var response = await _admin.GetAsync($"/api/workspaces/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("Secret");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_workspace_that_does_not_exist_gives_the_same_answer(string id)
    {
        var response = await _admin.GetAsync($"/api/workspaces/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldBe("{\"code\":\"not_found\"}");
    }

    [Fact]
    public async Task A_workspace_cannot_be_read_without_signing_in()
    {
        var id = await OwnWorkspaceIdAsync();

        var response = await _factory.CreateClient().GetAsync($"/api/workspaces/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Losing_the_membership_closes_the_workspace_at_once()
    {
        var id = await AddWorkspaceOfSomeoneElseAsync("Temporary", WorkspaceRole.Viewer);
        (await _admin.GetAsync($"/api/workspaces/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var adminId = await AdminIdAsync();

        await UseDbAsync(db => db.WorkspaceMembers.Where(m => m.WorkspaceId == id && m.UserId == adminId).ExecuteDeleteAsync());

        (await _admin.GetAsync($"/api/workspaces/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

[Collection(PostgresCollection.Name)]
public class MembershipBackfillTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_migration_makes_every_existing_workspace_owner_a_member()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        var before = db.Database.GetMigrations().Single(m => m.EndsWith("_AddSessions", StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(before);

        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO users (id, email, normalized_email, display_name, created_at, is_admin)
            VALUES ({userId}, 'old@example.com', 'old@example.com', 'Old', now(), true)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO workspaces (id, name, owner_id, created_at) VALUES ({workspaceId}, 'Old workspace', {userId}, '2026-01-02T03:04:05Z')
            """);

        await db.Database.MigrateAsync();

        var member = await db.WorkspaceMembers.AsNoTracking().SingleAsync();
        member.WorkspaceId.ShouldBe(workspaceId);
        member.UserId.ShouldBe(userId);
        member.Role.ShouldBe(WorkspaceRole.Owner);
        member.CreatedAt.ShouldBe(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
    }
}
