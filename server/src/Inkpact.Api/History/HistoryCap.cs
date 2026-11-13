using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

/// <summary>
/// What the capture and a restore both do when they write versions (run B.5b): make room under the workspace's cap, oldest
/// unprotected versions first, and give a new version a time that keeps a page's versions in the order they were written.
/// Every call is made under the workspace's history lock (<see cref="WorkspaceHistoryLock"/>).
/// </summary>
internal static class HistoryCap
{
    internal const int EvictionBatchSize = 128;

    /// <summary>The workspace's versions: all of them, and those protected now, which no cap removes.</summary>
    internal static IQueryable<PageVersion> VersionsOf(InkpactDbContext db, Guid workspaceId) =>
        db.PageVersions.Where(v => db.Pages.Any(p => p.Id == v.PageId && p.WorkspaceId == workspaceId));

    /// <summary>The bytes the workspace's versions take, and how many of them are protected at <paramref name="now"/>.</summary>
    internal static async Task<(long Size, long Protected)> MeasureAsync(
        InkpactDbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = VersionsOf(db, workspaceId);
        var size = await stored.SumAsync(v => (long?)v.PayloadBytes, cancellationToken) ?? 0;
        var kept = await stored.Where(v => v.ProtectedUntil > now).SumAsync(v => (long?)v.PayloadBytes, cancellationToken) ?? 0;
        return (size, kept);
    }

    /// <summary>
    /// Removes the workspace's oldest versions that are not protected until <paramref name="excess"/> bytes are gone. The
    /// caller has checked (with <see cref="MeasureAsync"/>, under the lock) that there are that many.
    /// </summary>
    internal static async Task EvictAsync(
        InkpactDbContext db, Guid workspaceId, long excess, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = VersionsOf(db, workspaceId);
        while (excess > 0)
        {
            var oldest = await stored.AsNoTracking()
                .Where(v => v.PayloadBytes > 0 && (v.ProtectedUntil == null || v.ProtectedUntil <= now))
                .OrderBy(v => v.CreatedAt)
                .ThenBy(v => v.Id)
                .Select(v => new { v.Id, v.PayloadBytes })
                .Take(EvictionBatchSize)
                .ToListAsync(cancellationToken);
            if (oldest.Count == 0)
            {
                throw new InvalidOperationException("The history has fewer unprotected bytes than were counted under its lock.");
            }

            var ids = new List<Guid>();
            foreach (var version in oldest)
            {
                ids.Add(version.Id);
                excess -= version.PayloadBytes;
                if (excess <= 0)
                {
                    break;
                }
            }

            await stored.Where(v => ids.Contains(v.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The time a new version gets: <paramref name="at"/>, or just after the page's newest version when that is not
    /// earlier. A page's versions are read newest first, so the one written last must also be the last in time: a rename
    /// that waited for the capture after it would otherwise sort before the version that capture made, and the page's
    /// newest version would carry a title the page no longer has. The smallest step Postgres keeps is a microsecond.
    /// </summary>
    internal static DateTimeOffset After(DateTimeOffset at, ref DateTimeOffset? last)
    {
        if (last is { } newest && at <= newest)
        {
            at = newest.AddTicks(TimeSpan.TicksPerMicrosecond);
        }

        last = at;
        return at;
    }

    /// <summary>What makes two versions of a page the same to someone looking at them: the document, the title and the file text.</summary>
    internal sealed record Shown(byte[] Fingerprint, string Title, string FileText)
    {
        public bool Same(Shown? other) =>
            other is not null && Title == other.Title && FileText == other.FileText
            && Fingerprint.Length > 0 && Fingerprint.AsSpan().SequenceEqual(other.Fingerprint);
    }

    /// <summary>The time of the page's newest version, if it has one: where <see cref="After"/> starts.</summary>
    internal static async Task<DateTimeOffset?> NewestAtAsync(InkpactDbContext db, Guid pageId, CancellationToken cancellationToken) =>
        await db.PageVersions.AsNoTracking()
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => (DateTimeOffset?)v.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}
