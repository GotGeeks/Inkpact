using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

/// <summary>Serializes a workspace's history policy, captures, restores and retention changes.</summary>
internal static class WorkspaceHistoryLock
{
    internal static async Task<bool> TryTakeAsync(InkpactDbContext db, Guid workspaceId, CancellationToken cancellationToken) =>
        await db.Workspaces.Where(w => w.Id == workspaceId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.HistoryEnabled, w => w.HistoryEnabled), cancellationToken) == 1;

    internal static async Task TakeAsync(InkpactDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!await TryTakeAsync(db, workspaceId, cancellationToken))
        {
            throw new InvalidOperationException("The workspace no longer exists.");
        }
    }
}
