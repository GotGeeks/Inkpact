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
    /// Everything a client merges to get the page, oldest first. Read in one repeatable-read transaction: once
    /// compaction exists (run 1.10b) this also reads the snapshot, and reading the two separately can miss the updates
    /// a compaction moves out of the log between the reads.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> LoadAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var updates = await db.PageUpdates.AsNoTracking()
            .Where(u => u.PageId == pageId)
            .OrderBy(u => u.Seq)
            .Select(u => u.Data)
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updates;
    }
}
