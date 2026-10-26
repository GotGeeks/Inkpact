using System.Data;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Sync;

/// <summary>The append-only log of a page's Yjs updates. The bytes are stored and returned as they came.</summary>
public sealed class PageUpdateStore(IDbContextFactory<InkpactDbContext> dbFactory, TimeProvider time)
{
    /// <summary>Appends one update and returns its sequence number.</summary>
    public async Task<long> AppendAsync(Guid pageId, Guid userId, byte[] data, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var update = new PageUpdate { PageId = pageId, UserId = userId, Data = data, CreatedAt = time.GetUtcNow() };
        db.PageUpdates.Add(update);
        await db.SaveChangesAsync(cancellationToken);
        return update.Seq;
    }

    /// <summary>
    /// The page as stored: its snapshot (if a compaction has made one) and the updates still live. Read in one
    /// repeatable-read transaction, because a compaction moves updates out of the log and into the snapshot in one
    /// transaction, and reading the two separately can miss them in between.
    /// </summary>
    public async Task<PageContent> LoadAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var snapshot = await db.PageSnapshots.AsNoTracking()
            .Where(s => s.PageId == pageId)
            .Select(s => s.State)
            .SingleOrDefaultAsync(cancellationToken);
        // All live rows, not those above the watermark: a row still in the log has not been merged, and rows can commit
        // out of sequence order (see the compactor).
        var updates = await db.PageUpdates.AsNoTracking()
            .Where(u => u.PageId == pageId)
            .OrderBy(u => u.Seq)
            .Select(u => u.Data)
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PageContent(snapshot, updates);
    }
}

/// <summary>A page's snapshot and the live updates after it.</summary>
public sealed record PageContent(byte[]? Snapshot, IReadOnlyList<byte[]> Updates)
{
    /// <summary>What to hand the codec, or to send a client in log mode: the snapshot first, then the updates.</summary>
    public IReadOnlyList<byte[]> Inputs => Snapshot is null ? Updates : [Snapshot, .. Updates];
}
