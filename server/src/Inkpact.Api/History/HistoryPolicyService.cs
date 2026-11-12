using System.Text.Json.Serialization;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

public sealed record HistoryPolicyDto(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] int GranularHours,
    [property: JsonRequired] int DailyDays,
    [property: JsonRequired] int ExpiryDays,
    [property: JsonRequired] long? CapMiB)
{
    internal static HistoryPolicyDto From(HistoryPolicy policy) =>
        new(policy.Enabled, policy.GranularHours, policy.DailyDays, policy.ExpiryDays, policy.CapBytes / HistoryPolicy.Mebibyte);

    internal HistoryPolicy? ToPolicy()
    {
        if (CapMiB is <= 0 or > long.MaxValue / HistoryPolicy.Mebibyte)
        {
            return null;
        }

        var policy = new HistoryPolicy(Enabled, GranularHours, DailyDays, ExpiryDays, CapMiB * HistoryPolicy.Mebibyte);
        return policy.IsValid ? policy : null;
    }
}

internal enum HistoryPolicyUpdateStatus
{
    Saved,
    Invalid,
    NotFound,
    Forbidden,
    ProtectedExceedsCap,
}

internal sealed record HistoryPolicyUpdate(HistoryPolicyUpdateStatus Status, HistoryPolicyDto? Policy = null);

internal sealed class HistoryPolicyService(InkpactDbContext db, IPermissionService permissions, TimeProvider time)
{
    internal async Task<HistoryPolicyDto?> ReadAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null)
        {
            return null;
        }

        var policy = HistoryPolicy.From(workspace);
        if (!policy.IsValid)
        {
            throw new InvalidOperationException("The workspace history policy is invalid.");
        }

        return HistoryPolicyDto.From(policy);
    }

    internal async Task<HistoryPolicyUpdate> UpdateAsync(
        Guid workspaceId, Guid userId, HistoryPolicyDto request, CancellationToken cancellationToken)
    {
        var policy = request.ToPolicy();
        if (policy is null)
        {
            return new(HistoryPolicyUpdateStatus.Invalid);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await WorkspaceHistoryLock.TryTakeAsync(db, workspaceId, cancellationToken))
        {
            return new(HistoryPolicyUpdateStatus.NotFound);
        }

        var role = await permissions.LockWorkspaceRoleAsync(userId, workspaceId, cancellationToken);
        if (role == WorkspaceRole.None)
        {
            return new(HistoryPolicyUpdateStatus.NotFound);
        }

        if (role < WorkspaceRole.Owner)
        {
            return new(HistoryPolicyUpdateStatus.Forbidden);
        }

        if (policy.CapBytes is { } cap)
        {
            var now = time.GetUtcNow().ToUniversalTime();
            var protectedBytes = await db.PageVersions
                .Where(v => v.ProtectedUntil > now && db.Pages.Any(p => p.Id == v.PageId && p.WorkspaceId == workspaceId))
                .SumAsync(v => (long?)v.PayloadBytes, cancellationToken) ?? 0;
            if (protectedBytes > cap)
            {
                return new(HistoryPolicyUpdateStatus.ProtectedExceedsCap);
            }
        }

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, cancellationToken);
        workspace.HistoryEnabled = policy.Enabled;
        workspace.HistoryGranularHours = policy.GranularHours;
        workspace.HistoryDailyDays = policy.DailyDays;
        workspace.HistoryExpiryDays = policy.ExpiryDays;
        workspace.HistoryCapBytes = policy.CapBytes;
        await db.SaveChangesAsync(cancellationToken);

        // A page that got no version because the cap left no room is tried again at once: the cap, or what the windows
        // keep, may be what just changed. Whether there is room now is for the capture to find out.
        await db.PageHistoryStates
            .Where(s => s.Failure == PageHistoryFailures.Full && db.Pages.Any(p => p.Id == s.PageId && p.WorkspaceId == workspaceId))
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.Failure, (string?)null)
                    .SetProperty(x => x.FailedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.FailedAttempts, 0),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(HistoryPolicyUpdateStatus.Saved, HistoryPolicyDto.From(policy));
    }
}
