using Npgsql;
using Spike.Crdt;

namespace Spike.Server;

/// <summary>The points in a compaction where a test can inject a crash, or block to let appends in.</summary>
public enum CompactionStage
{
    Locked,
    Read,
    Merged,
    Verified,
    SnapshotWritten,
    Archived,
    Deleted,
}

public enum CompactionResult
{
    Compacted,

    /// <summary>Another compaction of this page holds the lock; the next round picks it up.</summary>
    Skipped,

    /// <summary>No live updates: the page is already compacted, so running again changes nothing.</summary>
    NothingToDo,
}

/// <summary>The merged snapshot differs from an independent replay; nothing was changed.</summary>
public sealed class CompactionVerificationException(string message) : Exception(message);

/// <summary>
/// Folds a page's live updates into its snapshot (architecture → "Compaction guarantees"):
/// one transaction, advisory lock per page, watermark, verified against a replay, archive instead of delete.
/// </summary>
public sealed class Compactor(NpgsqlDataSource dataSource, ICrdtCodec codec, ILogger<Compactor> logger)
{
    /// <summary>Test hook, called after each stage. Throw to simulate a crash; await to block.</summary>
    public Func<CompactionStage, Task>? StageHook { get; set; }

    /// <summary>
    /// Also replay archive plus live updates without any snapshot, which catches a wrong earlier
    /// snapshot. Cost grows with the page's history, so the real app would sample it (run 1.10).
    /// </summary>
    public bool VerifyAgainstFullHistory { get; set; } = true;

    public async Task<CompactionResult> CompactAsync(string pageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        // Disposing the transaction without a commit rolls it back: every early exit and crash leaves no trace.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await TryLockAsync(connection, transaction, pageId, cancellationToken))
        {
            return CompactionResult.Skipped;
        }

        await RunStageAsync(CompactionStage.Locked);

        var (snapshot, version, previousCount) = await ReadSnapshotAsync(connection, transaction, pageId, cancellationToken);
        var (ids, updates) = await ReadLiveUpdatesAsync(connection, transaction, pageId, cancellationToken);
        await RunStageAsync(CompactionStage.Read);

        if (ids.Count == 0)
        {
            return CompactionResult.NothingToDo;
        }

        // Updates that arrive after the read above have a higher id and stay live.
        var watermark = ids[^1];
        IReadOnlyList<byte[]> inputs = snapshot is null ? updates : [snapshot, .. updates];

        var merged = codec.Merge(inputs);
        await RunStageAsync(CompactionStage.Merged);

        await VerifyAsync(connection, transaction, pageId, inputs, updates, merged, cancellationToken);
        await RunStageAsync(CompactionStage.Verified);

        await WriteSnapshotAsync(connection, transaction, pageId, merged, watermark, version + 1, previousCount + updates.Count, cancellationToken);
        await RunStageAsync(CompactionStage.SnapshotWritten);

        await ExecuteAsync(connection, transaction, """
            insert into page_updates_archive (id, page_id, data, created_at)
            select id, page_id, data, created_at from page_updates where page_id = $1 and id <= $2
            """, cancellationToken, pageId, watermark);
        await RunStageAsync(CompactionStage.Archived);

        await ExecuteAsync(connection, transaction,
            "delete from page_updates where page_id = $1 and id <= $2", cancellationToken, pageId, watermark);
        await RunStageAsync(CompactionStage.Deleted);

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Compacted page {PageId}: {Count} updates up to {Watermark} folded into snapshot v{Version} ({Bytes} bytes)",
            pageId, updates.Count, watermark, version + 1, merged.Length);
        return CompactionResult.Compacted;
    }

    private Task RunStageAsync(CompactionStage stage) => StageHook?.Invoke(stage) ?? Task.CompletedTask;

    /// <summary>The merged result must equal an independent replay in text and state vector, or nothing is committed.</summary>
    private async Task VerifyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string pageId,
        IReadOnlyList<byte[]> inputs,
        IReadOnlyList<byte[]> liveUpdates,
        byte[] merged,
        CancellationToken cancellationToken)
    {
        // Compared as CRDT documents (same updates integrated, same deletions), not as text: a page does
        // not have to be a TipTap document for its compaction to be checked.
        var mergedFingerprint = codec.Fingerprint(merged);

        // Same inputs, applied one transaction per update.
        AssertSame(pageId, "replay of snapshot and updates", mergedFingerprint, codec.Replay(inputs));

        if (VerifyAgainstFullHistory)
        {
            // No snapshot at all: every update ever appended, archived or live, in order.
            var archived = await ReadDataAsync(connection, transaction,
                "select data from page_updates_archive where page_id = $1 order by id", cancellationToken, pageId);
            AssertSame(pageId, "replay of the full history", mergedFingerprint, codec.Replay([.. archived, .. liveUpdates]));
        }
    }

    private void AssertSame(string pageId, string against, byte[] mergedFingerprint, byte[] other)
    {
        if (!codec.Fingerprint(other).AsSpan().SequenceEqual(mergedFingerprint))
        {
            logger.LogError("Compaction of page {PageId} aborted: the merged snapshot differs from the {Against}", pageId, against);
            throw new CompactionVerificationException($"Merged snapshot of page {pageId} differs from the {against}");
        }
    }

    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string pageId, CancellationToken cancellationToken)
    {
        // Held until this transaction ends; a second compaction of the page does not wait, it skips.
        await using var command = new NpgsqlCommand("select pg_try_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<(byte[]? Snapshot, int Version, long UpdateCount)> ReadSnapshotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string pageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select state, version, update_count from page_snapshots where page_id = $1 for update", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetFieldValue<byte[]>(0), reader.GetInt32(1), reader.GetInt64(2))
            : (null, 0, 0);
    }

    private static async Task<(List<long> Ids, List<byte[]> Updates)> ReadLiveUpdatesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string pageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select id, data from page_updates where page_id = $1 order by id", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });

        List<long> ids = [];
        List<byte[]> updates = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
            updates.Add(reader.GetFieldValue<byte[]>(1));
        }

        return (ids, updates);
    }

    private static async Task<List<byte[]>> ReadDataAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        List<byte[]> data = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            data.Add(reader.GetFieldValue<byte[]>(0));
        }

        return data;
    }

    private static Task WriteSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string pageId,
        byte[] state,
        long watermark,
        int version,
        long updateCount,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            insert into page_snapshots (page_id, state, watermark, version, update_count)
            values ($1, $2, $3, $4, $5)
            on conflict (page_id) do update set
              state = excluded.state, watermark = excluded.watermark, version = excluded.version,
              update_count = excluded.update_count, updated_at = now()
            """, cancellationToken, pageId, state, watermark, version, updateCount);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
