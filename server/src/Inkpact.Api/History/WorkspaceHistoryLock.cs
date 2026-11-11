using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

/// <summary>
/// Serializes a workspace's history policy, captures, restores and retention changes. It is an advisory lock for the
/// current transaction, not a lock on the workspace row: tree operations lock that row, and history work must neither
/// wait for them nor hold them up.
/// </summary>
internal static class WorkspaceHistoryLock
{
    /// <summary>
    /// The first key of the advisory lock; the second is <c>hashtext(workspace id)</c>. Fixed and different from the
    /// compactor's, so the two never meet.
    /// </summary>
    internal const int LockClass = 0x48495354;

    /// <summary>Takes the lock, waiting for whoever has it; false when the workspace no longer exists.</summary>
    internal static async Task<bool> TryTakeAsync(InkpactDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Taking the history lock requires a transaction.");
        }

        await db.Database.ExecuteSqlAsync(
            $"select pg_advisory_xact_lock({LockClass}, hashtext({workspaceId.ToString()}))", cancellationToken);
        return await db.Workspaces.AnyAsync(w => w.Id == workspaceId, cancellationToken);
    }

    internal static async Task TakeAsync(InkpactDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!await TryTakeAsync(db, workspaceId, cancellationToken))
        {
            throw new InvalidOperationException("The workspace no longer exists.");
        }
    }
}
