using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Compaction;

/// <summary>
/// Empties the archive of updates that were compacted longer ago than the retention. Run by the worker; the archive is
/// the safety net of phase 1A, kept so that a bug in a compaction can still be put right by hand.
/// </summary>
public sealed class ArchivePurge(
    IDbContextFactory<InkpactDbContext> dbFactory,
    CompactionOptions options,
    TimeProvider time,
    ILogger<ArchivePurge> logger)
{
    /// <summary>Deletes the expired rows, a batch to a transaction, and returns how many went.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - options.ArchiveRetention;
        var purged = 0;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var expired = await db.ArchivedPageUpdates.AsNoTracking()
                .Where(a => a.ArchivedAt < cutoff)
                .OrderBy(a => a.Seq)
                .Select(a => new { a.Seq, a.PageId })
                .Take(options.PurgeBatchSize)
                .ToListAsync(cancellationToken);
            if (expired.Count == 0)
            {
                return purged;
            }

            // First the flag, then the rows. The flag update takes the snapshot's row lock, so it waits for a compaction
            // that is running on the page (which holds that lock) and a compaction that starts later sees the flag: the
            // full-history check never meets a shortened archive while believing it is complete.
            var pages = expired.Select(a => a.PageId).Distinct().ToList();
            await db.PageSnapshots
                .Where(s => pages.Contains(s.PageId))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.HistoryComplete, false), cancellationToken);

            var seqs = expired.Select(a => a.Seq).ToList();
            await db.ArchivedPageUpdates.Where(a => seqs.Contains(a.Seq)).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            purged += seqs.Count;
            logger.LogInformation("Purged {Count} archived updates of {Pages} pages", seqs.Count, pages.Count);
        }
    }
}
