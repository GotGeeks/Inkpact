using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

internal sealed record HistoryRetentionResult(long Deleted, bool AboveCap);

/// <summary>Thins historical versions without touching the live snapshot, updates or compaction archive.</summary>
internal sealed class HistoryRetentionJob(
    IDbContextFactory<InkpactDbContext> dbFactory,
    TimeProvider time,
    ILogger<HistoryRetentionJob> logger)
{
    internal const int DeleteBatchSize = 128;
    internal const int PageBatchSize = 64;

    internal async Task<HistoryRetentionResult> PruneAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        long deleted = 0;
        Guid? after = null;
        while (true)
        {
            var pages = await PageIdsAsync(workspaceId, after, cancellationToken);
            if (pages.Count == 0)
            {
                break;
            }

            foreach (var pageId in pages)
            {
                deleted += await PrunePageAsync(workspaceId, pageId, cancellationToken);
            }

            after = pages[^1];
        }

        var (capDeleted, aboveCap) = await PruneToCapAsync(workspaceId, cancellationToken);
        deleted += capDeleted;
        if (aboveCap)
        {
            logger.LogWarning("History for workspace {WorkspaceId} exceeds its cap because the remaining versions are protected", workspaceId);
        }

        if (deleted > 0)
        {
            logger.LogInformation("Pruned {Count} historical versions for workspace {WorkspaceId}", deleted, workspaceId);
        }

        return new HistoryRetentionResult(deleted, aboveCap);
    }

    private async Task<List<Guid>> PageIdsAsync(Guid workspaceId, Guid? after, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var pages = db.Pages.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && db.PageVersions.Any(v => v.PageId == p.Id));
        if (after is { } last)
        {
            pages = pages.Where(p => p.Id.CompareTo(last) > 0);
        }

        return await pages.OrderBy(p => p.Id).Select(p => p.Id).Take(PageBatchSize).ToListAsync(cancellationToken);
    }

    private async Task<long> PrunePageAsync(Guid workspaceId, Guid pageId, CancellationToken cancellationToken)
    {
        long deleted = 0;
        HistoryPolicy? lastPolicy = null;
        DateTimeOffset now = default;
        DateTimeOffset? beforeTime = null;
        Guid beforeId = default;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await WorkspaceHistoryLock.TakeAsync(db, workspaceId, cancellationToken);
            var policy = await ReadPolicyAsync(db, workspaceId, cancellationToken);
            if (policy != lastPolicy)
            {
                now = time.GetUtcNow().ToUniversalTime();
                lastPolicy = policy;
                beforeTime = null;
            }

            var granular = TimeSpan.FromHours(policy.GranularHours);
            if (now - DateTimeOffset.MinValue < granular)
            {
                return deleted;
            }

            var candidates = db.PageVersions.AsNoTracking()
                .Where(v => v.PageId == pageId && v.CreatedAt <= now - granular
                    && db.Pages.Any(p => p.Id == pageId && p.WorkspaceId == workspaceId));
            var rule = new HistoryRetentionRule(policy, now);
            if (beforeTime is { } previousTime)
            {
                // Another transaction may have removed the bucket winner since the preceding batch.
                var previous = await candidates.Where(v =>
                        v.CreatedAt > previousTime || (v.CreatedAt == previousTime && v.Id.CompareTo(beforeId) >= 0))
                    .OrderBy(v => v.CreatedAt).ThenBy(v => v.Id)
                    .Select(v => new { v.CreatedAt, v.ProtectedUntil })
                    .FirstOrDefaultAsync(cancellationToken);
                if (previous is not null)
                {
                    rule.ShouldDelete(previous.CreatedAt, previous.ProtectedUntil);
                }

                candidates = candidates.Where(v =>
                    v.CreatedAt < previousTime || (v.CreatedAt == previousTime && v.Id.CompareTo(beforeId) < 0));
            }

            var batch = await candidates.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
                .Select(v => new { v.Id, v.CreatedAt, v.ProtectedUntil })
                .Take(DeleteBatchSize)
                .ToListAsync(cancellationToken);
            var ids = batch.Where(v => rule.ShouldDelete(v.CreatedAt, v.ProtectedUntil)).Select(v => v.Id).ToList();
            if (ids.Count > 0)
            {
                deleted += await db.PageVersions
                    .Where(v => v.PageId == pageId && ids.Contains(v.Id) && (v.ProtectedUntil == null || v.ProtectedUntil <= now)
                        && db.Pages.Any(p => p.Id == pageId && p.WorkspaceId == workspaceId))
                    .ExecuteDeleteAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            if (batch.Count < DeleteBatchSize)
            {
                return deleted;
            }

            beforeTime = batch[^1].CreatedAt;
            beforeId = batch[^1].Id;
        }
    }

    private async Task<(long Deleted, bool AboveCap)> PruneToCapAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        long totalDeleted = 0;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await WorkspaceHistoryLock.TakeAsync(db, workspaceId, cancellationToken);
            var policy = await ReadPolicyAsync(db, workspaceId, cancellationToken);
            if (policy.CapBytes is not { } cap)
            {
                return (totalDeleted, false);
            }

            var versions = db.PageVersions.Where(v => db.Pages.Any(p => p.Id == v.PageId && p.WorkspaceId == workspaceId));
            var size = await versions.SumAsync(v => (long?)v.PayloadBytes, cancellationToken) ?? 0;
            if (size <= cap)
            {
                return (totalDeleted, false);
            }

            var now = time.GetUtcNow().ToUniversalTime();
            var candidates = await versions.AsNoTracking()
                .Where(v => v.PayloadBytes > 0 && (v.ProtectedUntil == null || v.ProtectedUntil <= now))
                .OrderBy(v => v.CreatedAt)
                .ThenBy(v => v.Id)
                .Select(v => new { v.Id, v.PayloadBytes })
                .Take(DeleteBatchSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                return (totalDeleted, true);
            }

            var ids = new List<Guid>(candidates.Count);
            long bytes = 0;
            foreach (var candidate in candidates)
            {
                ids.Add(candidate.Id);
                bytes += candidate.PayloadBytes;
                if (bytes >= size - cap)
                {
                    break;
                }
            }

            var deleted = await versions
                .Where(v => ids.Contains(v.Id) && (v.ProtectedUntil == null || v.ProtectedUntil <= now))
                .ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            totalDeleted += deleted;
        }
    }

    private static async Task<HistoryPolicy> ReadPolicyAsync(InkpactDbContext db, Guid workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId, cancellationToken);
        var policy = HistoryPolicy.From(workspace);
        return policy.IsValid ? policy : throw new InvalidOperationException("The workspace history policy is invalid.");
    }
}
