using Npgsql;

namespace Spike.Server;

/// <summary>Append-only log of opaque Yjs updates per page.</summary>
public interface IUpdateStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    Task AppendAsync(string pageId, byte[] update, CancellationToken cancellationToken);

    /// <summary>All updates of a page, oldest first.</summary>
    Task<IReadOnlyList<byte[]>> LoadAsync(string pageId, CancellationToken cancellationToken);

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
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(string pageId, byte[] update, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "insert into page_updates (page_id, data) values ($1, $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        command.Parameters.Add(new NpgsqlParameter { Value = update });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<byte[]>> LoadAsync(string pageId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select data from page_updates where page_id = $1 order by id");
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });

        var updates = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            updates.Add(reader.GetFieldValue<byte[]>(0));
        }

        return updates;
    }

    public async Task<long> CountAsync(string pageId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "select count(*) from page_updates where page_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = pageId });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
