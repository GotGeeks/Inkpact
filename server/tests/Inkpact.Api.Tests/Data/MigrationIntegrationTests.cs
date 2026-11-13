using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Inkpact.Api.Tests.Data;

public class MigrationIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
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

    [Theory]
    [InlineData(false)] // no database: as the e2e suite starts, or a server whose database is made by the app
    [InlineData(true)] // an empty database: as the Postgres container of docker-compose.yml makes one (POSTGRES_DB)
    public async Task Migrating_a_fresh_install_logs_no_warning_or_error(bool databaseExists)
    {
        // A fresh install starts one of these two ways: an error in its first log would send the person looking for a fault.
        var connectionString = databaseExists
            ? await postgres.CreateDatabaseAsync()
            : new NpgsqlConnectionStringBuilder(postgres.Container.GetConnectionString())
            {
                Database = $"never_created_{Guid.NewGuid():N}",
                Pooling = false,
            }.ConnectionString;
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        using var holder = new InkpactDbContextHolder(connectionString, loggerFactory);

        await CreateMigrator(holder, Substitute.For<IDatabaseBackup>()).MigrateAsync();

        (await holder.Db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        logs.Entries.ShouldContain(e => e.Category.StartsWith("Microsoft.EntityFrameworkCore")); // it really listened
        logs.WarningsAndErrors.ShouldBeEmpty();
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

        columns.Keys.Where(t => t != "__EFMigrationsHistory").Order().ShouldBe(["attachments", "audit_events", "favorites", "import_items", "imports", "login_throttles", "page_history_state", "page_links", "page_restores", "page_snapshots", "page_tags", "page_title_changes", "page_updates", "page_updates_archive", "page_versions", "page_visits", "pages", "reminders", "sessions", "tags", "users", "workspace_members", "workspaces"]);
        columns["users"].ShouldBe(["id", "email", "normalized_email", "display_name", "password_hash", "oidc_subject", "created_at", "is_admin"]);
        columns["workspaces"].ShouldBe(["id", "name", "owner_id", "created_at", "tree_version", "history_cap_bytes", "history_daily_days", "history_enabled", "history_expiry_days", "history_granular_hours"]);
        columns["workspace_members"].ShouldBe(["workspace_id", "user_id", "role", "created_at"]);
        columns["audit_events"].ShouldBe(["id", "occurred_at", "type", "user_id", "email", "ip", "user_agent", "workspace_id"]);
        columns["login_throttles"].ShouldBe(["key", "failures", "locked_until", "last_failure_at"]);
        columns["sessions"].ShouldBe(["id", "user_id", "ticket", "created_at", "last_seen_at", "expires_at", "user_agent"]);
        columns["page_updates"].ShouldBe(["seq", "page_id", "data", "user_id", "created_at"]);
        columns["page_updates_archive"].ShouldBe(["seq", "page_id", "data", "user_id", "created_at", "archived_at"]);
        columns["page_versions"].ShouldBe(["id", "page_id", "state", "title", "file_text", "created_at", "created_by", "kind", "protected_until", "payload_bytes", "content_seq", "fingerprint"]);
        columns["page_history_state"].ShouldBe(["page_id", "captured_seq", "captured_updates", "failed_attempts", "waiting_attempts", "next_attempt_at", "failure", "failed_at"]);
        columns["page_restores"].ShouldBe(["operation_id", "page_id", "version_id", "user_id", "before_version_id", "restore_version_id", "title", "page_version", "created_at"]);
        columns["page_title_changes"].ShouldBe(["id", "page_id", "title", "page_version", "changed_at", "changed_by"]);
        columns["attachments"].ShouldBe(["id", "page_id", "file_name", "content_type", "size", "sha256", "created_at", "created_by", "external_id", "external_source_type"]);
        columns["tags"].ShouldBe(["id", "workspace_id", "name", "normalized_name", "external_source_type", "external_id", "created_at"]);
        columns["page_tags"].ShouldBe(["page_id", "tag_id"]);
        columns["reminders"].ShouldBe(["id", "page_id", "user_id", "remind_at", "delivered_at", "created_at"]);
        columns["imports"].ShouldBe(["id", "workspace_id", "user_id", "source_type", "file_name", "file_size", "state", "error_code", "total_items", "processed_items", "created_at", "started_at", "finished_at", "counts", "report", "copy_title"]);
        columns["import_items"].ShouldBe(["id", "workspace_id", "source_type", "external_id", "kind", "inkpact_id", "content_hash", "state_vector", "import_id", "imported_at", "base", "converter_version", "page_fingerprint", "page_seq"]);
        columns["page_snapshots"].ShouldBe(["page_id", "state", "state_vector", "watermark", "version", "update_count", "history_complete", "updated_at"]);
        columns["page_links"].ShouldBe(["source_page_id", "target_page_id"]);
        columns["favorites"].ShouldBe(["user_id", "page_id", "created_at"]);
        columns["page_visits"].ShouldBe(["user_id", "page_id", "opened_at"]);
        columns["pages"].ShouldBe(
        [
            "id", "workspace_id", "parent_id", "title", "sort_key", "created_at", "updated_at", "deleted_at",
            "is_todo", "completed_at", "due_at", "external_source_type", "external_id", "version", "content_text", "tag_text", "title_vector", "file_text", "search_vector", "links_indexed",
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

    private sealed class InkpactDbContextHolder(string connectionString, ILoggerFactory? loggerFactory = null) : IDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public Inkpact.Data.InkpactDbContext Db { get; } = new(
            new DbContextOptionsBuilder<Inkpact.Data.InkpactDbContext>()
                .UseNpgsql(connectionString)
                .UseLoggerFactory(loggerFactory ?? NullLoggerFactory.Instance)
                .Options);

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
