using Inkpact.Api.Crdt;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Compaction;

public enum CompactionResult
{
    Compacted,

    /// <summary>Another compaction of this page holds the lock; the worker picks the page up again later.</summary>
    Skipped,

    /// <summary>No live updates: the page is already compacted, so running again changes nothing.</summary>
    NothingToDo,

    /// <summary>The sidecar cannot answer right now. Nothing was changed.</summary>
    Unavailable,

    /// <summary>The page's updates do not decode, or the merge did not verify. Nothing was changed.</summary>
    Failed,
}

/// <summary>The merged snapshot differs from an independent replay; nothing is committed.</summary>
public sealed class CompactionVerificationException(string message) : Exception(message);

/// <summary>
/// Folds a page's live updates into its snapshot, with every guarantee in the architecture's "Compaction guarantees":
/// one transaction, an advisory lock per page, a watermark, verified against a replay, and the merged updates archived
/// instead of deleted. Disposing the transaction without committing rolls everything back, so every early exit and
/// every crash leaves no trace.
/// </summary>
public sealed class Compactor(
    IDbContextFactory<InkpactDbContext> dbFactory,
    IYjsCodec codec,
    ICompactionHooks hooks,
    CompactionOptions options,
    TimeProvider time,
    ILogger<Compactor> logger)
{
    /// <summary>The first key of the advisory lock; the second is the page. Fixed, so it never meets another use of the locks.</summary>
    private const int LockClass = 0x434F4D50;

    public async Task<CompactionResult> CompactAsync(Guid pageId, CancellationToken cancellationToken)
    {
        try
        {
            return await CompactCoreAsync(pageId, cancellationToken);
        }
        catch (YjsUnavailableException ex)
        {
            logger.LogDebug(ex, "Compaction of page {PageId} waits: the Yjs sidecar is not available", pageId);
            return CompactionResult.Unavailable;
        }
        catch (YjsRejectedException ex)
        {
            logger.LogError(ex, "Compaction of page {PageId} failed: Yjs cannot decode one of its updates", pageId);
            return CompactionResult.Failed;
        }
        catch (CompactionVerificationException ex)
        {
            logger.LogError(ex, "Compaction of page {PageId} aborted: the merged snapshot does not verify", pageId);
            return CompactionResult.Failed;
        }
    }

    private async Task<CompactionResult> CompactCoreAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Held until this transaction ends. A second compaction of the page does not wait, it skips. Two pages whose ids
        // hash alike only cause a skip.
        var locked = await db.Database
            .SqlQuery<bool>($"select pg_try_advisory_xact_lock({LockClass}, hashtext({pageId.ToString()})) as \"Value\"")
            .SingleAsync(cancellationToken);
        if (!locked)
        {
            return CompactionResult.Skipped;
        }

        await hooks.AfterStageAsync(pageId, CompactionStage.Locked, cancellationToken);

        // For update: the purge changes this row too, so it waits for us and cannot shorten the archive between our
        // reading history_complete and reading the archive.
        var snapshot = (await db.PageSnapshots
            .FromSql($"select * from page_snapshots where page_id = {pageId} for update")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        var batch = await db.PageUpdates.AsNoTracking()
            .Where(u => u.PageId == pageId)
            .OrderBy(u => u.Seq)
            .Take(options.MaxUpdatesPerCompaction)
            .ToListAsync(cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.Read, cancellationToken);

        if (batch.Count == 0)
        {
            return CompactionResult.NothingToDo;
        }

        IReadOnlyList<byte[]> inputs = snapshot is null ? batch.Select(u => u.Data).ToList() : [snapshot.State, .. batch.Select(u => u.Data)];
        var merged = await codec.CompactAsync(inputs, cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.Merged, cancellationToken);

        var version = (snapshot?.Version ?? 0) + 1;
        await VerifyAsync(db, pageId, snapshot, version, batch, merged, cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.Verified, cancellationToken);

        // Exactly the rows that were read go. A row that was appended after the read stays live, and so does one with a
        // lower seq that commits late (seq is a sequence, so commit order can differ from seq order). Deleting
        // "seq <= watermark" would lose such a row. The watermark itself never moves back.
        var now = time.GetUtcNow();
        var watermark = Math.Max(snapshot?.Watermark ?? 0, batch[^1].Seq);
        if (snapshot is null)
        {
            snapshot = new PageSnapshot { PageId = pageId };
            db.PageSnapshots.Add(snapshot);
        }

        snapshot.State = merged.State;
        snapshot.StateVector = merged.StateVector;
        snapshot.Watermark = watermark;
        snapshot.Version = version;
        snapshot.UpdateCount += batch.Count;
        snapshot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.SnapshotWritten, cancellationToken);

        db.ArchivedPageUpdates.AddRange(batch.Select(u => new ArchivedPageUpdate
        {
            Seq = u.Seq,
            PageId = u.PageId,
            Data = u.Data,
            UserId = u.UserId,
            CreatedAt = u.CreatedAt,
            ArchivedAt = now,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.Archived, cancellationToken);

        var seqs = batch.Select(u => u.Seq).ToList();
        await db.PageUpdates.Where(u => u.PageId == pageId && seqs.Contains(u.Seq)).ExecuteDeleteAsync(cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.Deleted, cancellationToken);

        // The text only; the page's version and updated_at belong to its title and its place in the tree. Postgres text
        // cannot hold a NUL character, which a document can.
        var text = merged.Text.Replace("\0", "");
        await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(p => p.SetProperty(x => x.ContentText, text), cancellationToken);
        await hooks.AfterStageAsync(pageId, CompactionStage.TextWritten, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Compacted page {PageId}: {Count} updates folded into snapshot v{Version} ({Bytes} bytes)",
            pageId, batch.Count, version, merged.State.Length);
        return CompactionResult.Compacted;
    }

    /// <summary>The merged state must equal a replay of its inputs, and now and then of the page's whole history, or nothing is committed.</summary>
    private async Task VerifyAsync(
        InkpactDbContext db, Guid pageId, PageSnapshot? snapshot, int version, List<PageUpdate> batch, YjsCompaction merged,
        CancellationToken cancellationToken)
    {
        if (!merged.StateFingerprint.AsSpan().SequenceEqual(merged.ReplayFingerprint))
        {
            throw new CompactionVerificationException(
                $"The merged state of page {pageId} differs from a replay of its snapshot and updates "
                + $"({merged.StateFingerprint.Length} and {merged.ReplayFingerprint.Length} bytes of fingerprint)");
        }

        // A wrong earlier snapshot is only caught by replaying every update the page ever had, which costs more the
        // longer the page lives, so it is sampled. Not when the purge has taken part of the history.
        var due = snapshot is { HistoryComplete: true } && version % options.FullHistoryEvery == 0;
        if (!due)
        {
            return;
        }

        var archived = await db.ArchivedPageUpdates.AsNoTracking()
            .Where(a => a.PageId == pageId)
            .OrderBy(a => a.Seq)
            .Select(a => a.Data)
            .ToListAsync(cancellationToken);
        var history = await codec.FingerprintAsync([.. archived, .. batch.Select(u => u.Data)], cancellationToken);
        if (!merged.StateFingerprint.AsSpan().SequenceEqual(history))
        {
            throw new CompactionVerificationException(
                $"The merged state of page {pageId} differs from a replay of its whole history "
                + $"({merged.StateFingerprint.Length} and {history.Length} bytes of fingerprint)");
        }
    }
}
