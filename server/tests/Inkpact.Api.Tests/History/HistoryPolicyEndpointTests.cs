using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.History;
using Inkpact.Api.Permissions;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.History;

public class HistoryPolicyEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly HistoryPolicyDto Default = new(true, 24, 30, 365, null);
    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;
    private Guid _ownerId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: new MutableTimeProvider(Now));
        _owner = await _factory.SignedInClientAsync();
        (_workspaceId, _ownerId) = await UseDbAsync(async db =>
        {
            var workspace = await db.Workspaces.SingleAsync();
            return (workspace.Id, workspace.OwnerId);
        });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task The_owner_can_read_and_replace_the_policy_even_without_pages()
    {
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(Default);
        var changed = new HistoryPolicyDto(false, 48, 60, 730, 8);

        var response = await PutAsync(_owner, _workspaceId, changed);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<HistoryPolicyDto>()).ShouldBe(changed);
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(changed);
        var stored = await UseDbAsync(db => db.Workspaces.AsNoTracking().SingleAsync());
        stored.HistoryEnabled.ShouldBeFalse();
        stored.HistoryCapBytes.ShouldBe(8 * HistoryPolicy.Mebibyte);

        (await PutAsync(_owner, _workspaceId, Default)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(Default);
    }

    [Fact]
    public async Task The_usage_counts_the_workspace_s_versions_and_what_of_them_is_protected_and_nothing_of_another()
    {
        (await _owner.GetFromJsonAsync<HistoryUsageDto>(UsagePath(_workspaceId))).ShouldBe(new HistoryUsageDto(0, 0));
        var other = await OtherWorkspaceAsync(WorkspaceRole.Viewer);
        await UseDbAsync(async db =>
        {
            var page = new Page { WorkspaceId = _workspaceId, Title = "Page", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now };
            var elsewhere = new Page { WorkspaceId = other, Title = "Elsewhere", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now };
            db.Pages.AddRange(page, elsewhere);
            await db.SaveChangesAsync();
            db.PageVersions.AddRange(
                new PageVersion(page.Id, new byte[100], "Page", "abc", Now, null, PageVersionKind.Automatic),
                new PageVersion(page.Id, new byte[50], "Page", "", Now, null, PageVersionKind.BeforeRestore, protectedUntil: Now.AddHours(1)),
                new PageVersion(page.Id, new byte[20], "Page", "", Now, null, PageVersionKind.BeforeRestore, protectedUntil: Now.AddHours(-1)),
                new PageVersion(elsewhere.Id, new byte[999], "Elsewhere", "", Now, null, PageVersionKind.Automatic));
            return await db.SaveChangesAsync();
        });

        (await _owner.GetFromJsonAsync<HistoryUsageDto>(UsagePath(_workspaceId))).ShouldBe(new HistoryUsageDto(173, 50));
        // A viewer of the other workspace reads its usage too; a stranger to it does not.
        (await _owner.GetFromJsonAsync<HistoryUsageDto>(UsagePath(other))).ShouldBe(new HistoryUsageDto(999, 0));
        (await _owner.GetAsync(UsagePath(Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string UsagePath(Guid workspaceId) => $"/api/workspaces/{workspaceId}/history-usage";

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Editor)]
    public async Task A_member_can_read_but_only_an_owner_can_change_the_policy(WorkspaceRole role)
    {
        var workspaceId = await OtherWorkspaceAsync(role);

        (await _owner.GetAsync(Path(workspaceId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await PutAsync(_owner, workspaceId, Default with { Enabled = false });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadErrorAsync()).Code.ShouldBe("forbidden");
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(workspaceId))).ShouldBe(Default);
    }

    [Fact]
    public async Task A_stranger_and_a_missing_workspace_do_not_reveal_the_policy()
    {
        var other = await OtherWorkspaceAsync(null);
        foreach (var workspaceId in new[] { other, Guid.NewGuid() })
        {
            var get = await _owner.GetAsync(Path(workspaceId));
            var put = await PutAsync(_owner, workspaceId, Default);
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await get.ReadErrorAsync()).Code.ShouldBe("not_found");
            (await put.ReadErrorAsync()).Code.ShouldBe("not_found");
        }
    }

    [Fact]
    public async Task Reading_requires_a_session_and_saving_requires_csrf()
    {
        (await _factory.CreateClient().GetAsync(Path(_workspaceId))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var withoutToken = await _owner.PutAsJsonAsync(Path(_workspaceId), Default with { Enabled = false });
        withoutToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await withoutToken.ReadErrorAsync()).Code.ShouldBe("csrf_invalid");
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(Default);
    }

    [Theory]
    [InlineData(0, 30, 365, null)]
    [InlineData(25, 1, 365, null)]
    [InlineData(24, 30, 29, null)]
    [InlineData(24, 30, 3651, null)]
    [InlineData(24, 30, 365, 0L)]
    [InlineData(24, 30, 365, -1L)]
    [InlineData(24, 30, 365, long.MaxValue)]
    public async Task Invalid_windows_and_caps_are_rejected_without_changing_the_policy(int hours, int days, int expiry, long? cap)
    {
        var response = await PutAsync(_owner, _workspaceId, new HistoryPolicyDto(false, hours, days, expiry, cap));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("history_policy_invalid");
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(Default);
    }

    [Fact]
    public async Task A_replacement_must_include_every_field_so_omissions_cannot_disable_or_clear_history()
    {
        var response = await PutAsync(_owner, _workspaceId, new { granularHours = 24, dailyDays = 30, expiryDays = 365 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(Default);
    }

    [Fact]
    public async Task A_cap_below_unexpired_protected_bytes_is_rejected_without_saving_any_field()
    {
        await UseDbAsync(async db =>
        {
            var page = new Page { WorkspaceId = _workspaceId, Title = "Page", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now };
            db.Add(page);
            db.PageVersions.Add(new PageVersion(page.Id, new byte[1_048_577], "Title", "", Now, null, PageVersionKind.BeforeRestore, Now.AddHours(1)));
            await db.SaveChangesAsync();
            return 0;
        });
        var request = new HistoryPolicyDto(false, 48, 60, 730, 1);

        var response = await PutAsync(_owner, _workspaceId, request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("history_cap_below_protected");
        (await _owner.GetFromJsonAsync<HistoryPolicyDto>(Path(_workspaceId))).ShouldBe(Default);
        (await PutAsync(_owner, _workspaceId, request with { CapMiB = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Protection_ending_now_and_protection_in_other_workspaces_do_not_prevent_a_cap()
    {
        var other = await OtherWorkspaceAsync(null);
        await UseDbAsync(async db =>
        {
            var page = new Page { WorkspaceId = _workspaceId, Title = "Page", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now };
            var otherPage = new Page { WorkspaceId = other, Title = "Other", SortKey = "a0", CreatedAt = Now, UpdatedAt = Now };
            db.AddRange(page, otherPage);
            db.PageVersions.AddRange(
                new PageVersion(page.Id, new byte[1_048_577], "Title", "", Now, null, PageVersionKind.BeforeRestore, Now),
                new PageVersion(otherPage.Id, new byte[1_048_577], "Title", "", Now, null, PageVersionKind.BeforeRestore, Now.AddHours(1)));
            await db.SaveChangesAsync();
            return 0;
        });

        (await PutAsync(_owner, _workspaceId, Default with { CapMiB = 1 })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Saving_rechecks_an_owner_role_even_if_the_permission_service_cached_it()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        var service = scope.ServiceProvider.GetRequiredService<HistoryPolicyService>();
        (await permissions.GetWorkspaceRoleAsync(_ownerId, _workspaceId)).ShouldBe(WorkspaceRole.Owner);
        await UseDbAsync(db => db.WorkspaceMembers
            .Where(m => m.WorkspaceId == _workspaceId && m.UserId == _ownerId)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.Role, WorkspaceRole.Editor)));

        var result = await service.UpdateAsync(_workspaceId, _ownerId, Default with { Enabled = false }, default);

        result.Status.ShouldBe(HistoryPolicyUpdateStatus.Forbidden);
        (await permissions.GetWorkspaceRoleAsync(_ownerId, _workspaceId)).ShouldBe(WorkspaceRole.Editor);
        await UseDbAsync(db => db.WorkspaceMembers
            .Where(m => m.WorkspaceId == _workspaceId && m.UserId == _ownerId).ExecuteDeleteAsync());
        (await service.UpdateAsync(_workspaceId, _ownerId, Default with { Enabled = false }, default)).Status.ShouldBe(HistoryPolicyUpdateStatus.NotFound);
        var saved = await UseDbAsync(db => db.Workspaces.AsNoTracking().SingleAsync());
        saved.HistoryEnabled.ShouldBeTrue();
    }

    private static string Path(Guid workspaceId) => $"/api/workspaces/{workspaceId}/history-policy";

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid workspaceId, object request)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, Path(workspaceId)) { Content = JsonContent.Create(request) };
        message.Headers.Add(CsrfProtection.HeaderName, (await client.GetStateAsync()).CsrfToken);
        return await client.SendAsync(message);
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private async Task<Guid> OtherWorkspaceAsync(WorkspaceRole? role)
    {
        return await UseDbAsync(async db =>
        {
            var other = new User { Email = "other@example.com", DisplayName = "Other", CreatedAt = Now };
            var workspace = new Workspace { Name = "Other", OwnerId = other.Id, CreatedAt = Now };
            db.AddRange(other, workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = other.Id, Role = WorkspaceRole.Owner, CreatedAt = Now });
            if (role is { } memberRole)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = _ownerId, Role = memberRole, CreatedAt = Now });
            }

            await db.SaveChangesAsync();
            return workspace.Id;
        });
    }
}
