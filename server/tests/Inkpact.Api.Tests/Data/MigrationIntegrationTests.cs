using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Inkpact.Api.Tests.Data;

[Collection(PostgresCollection.Name)]
public class MigrationIntegrationTests(PostgresFixture postgres)
{
    private static DatabaseMigrator CreateMigrator(InkpactDbContextHolder holder, IDatabaseBackup backup) =>
        new(new EfMigrationStore(holder.Db), backup, Options.Create(new DatabaseOptions()), NullLogger<DatabaseMigrator>.Instance);

    [Fact]
    public async Task Migrates_a_database_that_does_not_exist_yet()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(postgres.Container.GetConnectionString())
        {
            Database = $"never_created_{Guid.NewGuid():N}",
            Pooling = false,
        }.ConnectionString;
        using var holder = new InkpactDbContextHolder(connectionString);
        var backup = Substitute.For<IDatabaseBackup>();

        await CreateMigrator(holder, backup).MigrateAsync();

        var applied = await new EfMigrationStore(holder.Db).GetAppliedMigrationsAsync(default);
        applied.ShouldBe(new EfMigrationStore(holder.Db).GetKnownMigrations());
        await backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Running_twice_changes_nothing_the_second_time()
    {
        using var holder = new InkpactDbContextHolder(await postgres.CreateDatabaseAsync());
        var backup = Substitute.For<IDatabaseBackup>();
        var migrator = CreateMigrator(holder, backup);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
        (await holder.Db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_a_database_with_a_migration_from_a_newer_version()
    {
        using var holder = new InkpactDbContextHolder(await postgres.CreateDatabaseAsync());
        var backup = Substitute.For<IDatabaseBackup>();
        var migrator = CreateMigrator(holder, backup);
        await migrator.MigrateAsync();
        await holder.Db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('99991231235959_FromTheFuture', '99.0.0')");

        var exception = await Should.ThrowAsync<DatabaseNewerThanAppException>(() => migrator.MigrateAsync());

        exception.UnknownMigrations.ShouldBe(["99991231235959_FromTheFuture"]);
        await backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Creates_the_tables_of_the_architecture_with_snake_case_names()
    {
        using var holder = new InkpactDbContextHolder(await postgres.CreateDatabaseAsync());
        await CreateMigrator(holder, Substitute.For<IDatabaseBackup>()).MigrateAsync();

        var columns = new Dictionary<string, List<string>>();
        await using var connection = new NpgsqlConnection(holder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, ordinal_position",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!columns.TryGetValue(table, out var list))
            {
                columns[table] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        columns.Keys.Where(t => t != "__EFMigrationsHistory").Order().ShouldBe(["audit_events", "login_throttles", "page_updates", "pages", "sessions", "users", "workspace_members", "workspaces"]);
        columns["users"].ShouldBe(["id", "email", "normalized_email", "display_name", "password_hash", "oidc_subject", "created_at", "is_admin"]);
        columns["workspaces"].ShouldBe(["id", "name", "owner_id", "created_at", "tree_version"]);
        columns["workspace_members"].ShouldBe(["workspace_id", "user_id", "role", "created_at"]);
        columns["audit_events"].ShouldBe(["id", "occurred_at", "type", "user_id", "email", "ip", "user_agent"]);
        columns["login_throttles"].ShouldBe(["key", "failures", "locked_until", "last_failure_at"]);
        columns["sessions"].ShouldBe(["id", "user_id", "ticket", "created_at", "last_seen_at", "expires_at", "user_agent"]);
        columns["page_updates"].ShouldBe(["seq", "page_id", "data", "user_id", "created_at"]);
        columns["pages"].ShouldBe(
        [
            "id", "workspace_id", "parent_id", "title", "sort_key", "created_at", "updated_at", "deleted_at",
            "is_todo", "completed_at", "due_at", "external_source_type", "external_id", "version",
        ]);
    }

    [Fact]
    public async Task Pg_dump_writes_a_dump_that_contains_the_schema()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        using var holder = new InkpactDbContextHolder(connectionString);
        await CreateMigrator(holder, Substitute.For<IDatabaseBackup>()).MigrateAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"inkpact-dump-{Guid.NewGuid():N}");

        try
        {
            var backup = new PgDumpBackup(new ContainerPgDumpRunner(postgres), TimeProvider.System);
            var path = await backup.BackupAsync(connectionString, directory, "InitialCreate", default);

            var bytes = await File.ReadAllBytesAsync(path);
            System.Text.Encoding.ASCII.GetString(bytes, 0, 5).ShouldBe("PGDMP");
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            text.ShouldContain("pages");
            text.ShouldContain("__EFMigrationsHistory");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class InkpactDbContextHolder(string connectionString) : IDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public Inkpact.Data.InkpactDbContext Db { get; } = new(
            new DbContextOptionsBuilder<Inkpact.Data.InkpactDbContext>().UseNpgsql(connectionString).Options);

        public void Dispose() => Db.Dispose();
    }

    /// <summary>Runs pg_dump inside the Postgres container, so the test does not need the client tools on the host.</summary>
    private sealed class ContainerPgDumpRunner(PostgresFixture postgres) : IPgDumpRunner
    {
        public async Task RunAsync(PgDumpRequest request, CancellationToken cancellationToken)
        {
            const string inContainer = "/tmp/inkpact-test.dump";
            var result = await postgres.Container.ExecAsync(
                ["pg_dump", "--format=custom", "--username", request.Username, "--dbname", request.Database, "--file", inContainer],
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new BackupFailedException(result.Stderr);
            }

            await File.WriteAllBytesAsync(
                request.OutputPath, await postgres.Container.ReadFileAsync(inContainer, cancellationToken), cancellationToken);
        }
    }
}
