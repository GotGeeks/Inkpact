using System.Data;
using Npgsql;

namespace Spike.Server;

/// <summary>
/// A page as stored: the compacted snapshot (if any) plus the updates after it, read in one consistent view.
/// </summary>
public sealed record PageLoad(byte[]? Snapshot, long SnapshotUpdateCount, IReadOnlyList<byte[]> Updates)
{
    /// <summary>Snapshot first, then the newer updates: what a client merges to get the page.</summary>
    public IReadOnlyList<byte[]> All => Snapshot is null ? Updates : [Snapshot, .. Updates];

    /// <summary>How many appended updates this view contains, folded into the snapshot or not.</summary>
    public long AppliedUpdates => SnapshotUpdateCount + Updates.Count;
}

public sealed record PageStats(
    long SnapshotBytes,
    long SnapshotUpdateCount,
    int SnapshotVersion,
    long LiveUpdates,
    long LiveUpdateBytes,
    long ArchivedUpdates);

/// <summary>Append-only log of opaque Yjs updates per page, folded into snapshots by compaction.</summary>
public interface IUpdateStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Appends an update and returns its sequence number (the compaction watermark unit).</summary>
    Task<long> AppendAsync(string pageId, byte[] update, CancellationToken cancellationToken);

    /// <summary>The live (not yet compacted) updates of a page, oldest first.</summary>
    Task<IReadOnlyList<byte[]>> LoadAsync(string pageId, CancellationToken cancellationToken);

    /// <summary>
    /// Snapshot and newer updates in one repeatable-read view. Reading them separately could miss the
    /// updates a compaction commits in between: archived out of the log, but not yet in the snapshot we read.
    /// </summary>
    Task<PageLoad> LoadPageAsync(string pageId, CancellationToken cancellationToken);

    /// <summary>Pages with at least <paramref name="threshold"/> live updates.</summary>
    Task<IReadOnlyList<string>> PagesNeedingCompactionAsync(int threshold, CancellationToken cancellationToken);

    Task<PageStats> GetStatsAsync(string pageId, CancellationToken cancellationToken);

    /// <summary>Number of live updates of a page.</summary>
    Task<long> CountAsync(string pageId, CancellationToken cancellationToken);
}

/// <summary>Spike only: plain Npgsql and a schema created on startup; EF Core migrations arrive in 1.03.</summary>
public sealed class PostgresUpdateStore(NpgsqlDataSource dataSource) : IUpdateStore
{
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            create table if not exists page_updates (
              id bigint generated always as identity primary key,
              page_id text not null,
              data bytea not null,
              created_at timestamptz not null default now());
            create index if not exists page_updates_page_id_id on page_updates (page_id, id);

            -- One current row per page: all updates up to and including watermark, folded into state.
            create table if not exists page_snapshots (
              page_id text primary key,
              state bytea not null,
              watermark bigint not null,
              version int not null,
              update_count bigint not null,
              updated_at timestamptz not null default now());

            -- Compacted updates are kept (30 days in the real app; the purge is run 1.10).
            create table if not exists page_updates_archive (
              id bigint primary key,
              page_id text not null,
              data bytea not null,
              created_at timestamptz not null,
              archived_at timestamptz not null default now());
            create index if not exists page_updates_archive_page_id_id on page_updates_archive (page_id, id);
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> AppendAsync(string pageId, byte[] update, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "insert into page_updates (page_id, data) values ($1, $2) returning id");
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        command.Parameters.Add(new NpgsqlParameter { Value = update });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<byte[]>> LoadAsync(string pageId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select data from page_updates where page_id = $1 order by id");
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        return await ReadDataAsync(command, cancellationToken);
    }

    public async Task<PageLoad> LoadPageAsync(string pageId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        byte[]? snapshot = null;
        long watermark = 0;
        long updateCount = 0;
        await using (var command = new NpgsqlCommand(
            "select state, watermark, update_count from page_snapshots where page_id = $1", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = pageId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                snapshot = reader.GetFieldValue<byte[]>(0);
                watermark = reader.GetInt64(1);
                updateCount = reader.GetInt64(2);
            }
        }

        await using var updatesCommand = new NpgsqlCommand(
            "select data from page_updates where page_id = $1 and id > $2 order by id", connection, transaction);
        updatesCommand.Parameters.Add(new NpgsqlParameter { Value = pageId });
        updatesCommand.Parameters.Add(new NpgsqlParameter { Value = watermark });
        var updates = await ReadDataAsync(updatesCommand, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new PageLoad(snapshot, updateCount, updates);
    }

    public async Task<IReadOnlyList<string>> PagesNeedingCompactionAsync(int threshold, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select page_id from page_updates group by page_id having count(*) >= $1");
        command.Parameters.Add(new NpgsqlParameter { Value = threshold });

        var pages = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pages.Add(reader.GetString(0));
        }

        return pages;
    }

    public async Task<PageStats> GetStatsAsync(string pageId, CancellationToken cancellationToken)
    {
        // One statement, so one snapshot of the database: the numbers belong together.
        await using var command = dataSource.CreateCommand("""
            select
              coalesce((select length(state) from page_snapshots where page_id = $1), 0),
              coalesce((select update_count from page_snapshots where page_id = $1), 0),
              coalesce((select version from page_snapshots where page_id = $1), 0),
              (select count(*) from page_updates where page_id = $1),
              coalesce((select sum(length(data)) from page_updates where page_id = $1), 0),
              (select count(*) from page_updates_archive where page_id = $1)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new PageStats(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
    }

    public async Task<long> CountAsync(string pageId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from page_updates where page_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<IReadOnlyList<byte[]>> ReadDataAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var updates = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            updates.Add(reader.GetFieldValue<byte[]>(0));
        }

        return updates;
    }
}
