using Inkpact.Api.History;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.History;

public class WorkspaceHistoryLockTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private Guid _workspaceId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await using var db = CreateContext();
        var owner = new User { Email = "owner@example.com", DisplayName = "Owner", CreatedAt = Now };
        var workspace = new Workspace { Name = "Workspace", OwnerId = owner.Id, CreatedAt = Now };
        db.AddRange(owner, workspace);
        await db.SaveChangesAsync();
        _workspaceId = workspace.Id;
    }

    [Fact]
    public async Task The_lock_holds_up_other_history_work_but_not_a_tree_operation()
    {
        await using var holder = CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        (await WorkspaceHistoryLock.TryTakeAsync(holder, _workspaceId, default)).ShouldBeTrue();

        // A tree operation locks the workspace row, as TreeService does: it is not kept waiting.
        await using (var tree = CreateContext())
        {
            (await tree.Workspaces.Where(w => w.Id == _workspaceId)
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.TreeVersion, w => w.TreeVersion + 1))
                .WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(1);
        }

        await using var other = CreateContext();
        await using var waiting = await other.Database.BeginTransactionAsync();
        var second = WorkspaceHistoryLock.TryTakeAsync(other, _workspaceId, default);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        second.IsCompleted.ShouldBeFalse();

        await held.CommitAsync();
        (await second.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
    }

    [Fact]
    public async Task A_missing_workspace_is_reported_and_the_lock_needs_a_transaction()
    {
        await using var db = CreateContext();
        await Should.ThrowAsync<InvalidOperationException>(() => WorkspaceHistoryLock.TryTakeAsync(db, _workspaceId, default));

        await using var transaction = await db.Database.BeginTransactionAsync();
        (await WorkspaceHistoryLock.TryTakeAsync(db, Guid.NewGuid(), default)).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => WorkspaceHistoryLock.TakeAsync(db, Guid.NewGuid(), default));
    }
}
