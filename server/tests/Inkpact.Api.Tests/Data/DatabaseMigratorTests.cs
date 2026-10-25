using Inkpact.Data.Upgrade;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Inkpact.Api.Tests.Data;

public class DatabaseMigratorTests
{
    private readonly IMigrationStore _store = Substitute.For<IMigrationStore>();
    private readonly IDatabaseBackup _backup = Substitute.For<IDatabaseBackup>();
    private readonly DatabaseOptions _options = new() { BackupDirectory = "backups" };

    public DatabaseMigratorTests()
    {
        _store.ConnectionString.Returns("Host=db;Database=inkpact");
        _backup.BackupAsync(default!, default!, default!, default).ReturnsForAnyArgs("backups/x.dump");
    }

    private DatabaseMigrator CreateMigrator() =>
        new(_store, _backup, Options.Create(_options), NullLogger<DatabaseMigrator>.Instance);

    private void Setup(string[] known, string[] applied)
    {
        _store.GetKnownMigrations().Returns(known);
        _store.GetAppliedMigrationsAsync(Arg.Any<CancellationToken>()).Returns(applied);
    }

    [Fact]
    public async Task New_database_is_migrated_without_a_backup()
    {
        Setup(known: ["001", "002"], applied: []);

        await CreateMigrator().MigrateAsync();

        await _store.Received(1).ApplyPendingAsync(Arg.Any<CancellationToken>());
        await _backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Up_to_date_database_is_left_alone()
    {
        Setup(known: ["001", "002"], applied: ["001", "002"]);

        await CreateMigrator().MigrateAsync();

        await _store.DidNotReceive().ApplyPendingAsync(Arg.Any<CancellationToken>());
        await _backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Existing_database_with_pending_migrations_is_backed_up_first()
    {
        Setup(known: ["001", "002"], applied: ["001"]);
        var calls = new List<string>();
        _backup.BackupAsync(default!, default!, default!, default).ReturnsForAnyArgs(_ =>
        {
            calls.Add("backup");
            return Task.FromResult("backups/x.dump");
        });
        _store.ApplyPendingAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("migrate");
            return Task.CompletedTask;
        });

        await CreateMigrator().MigrateAsync();

        calls.ShouldBe(["backup", "migrate"]);
        await _backup.Received(1).BackupAsync(
            "Host=db;Database=inkpact", "backups", "001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backup_can_be_turned_off()
    {
        _options.BackupOnMigrate = false;
        Setup(known: ["001", "002"], applied: ["001"]);

        await CreateMigrator().MigrateAsync();

        await _store.Received(1).ApplyPendingAsync(Arg.Any<CancellationToken>());
        await _backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Failed_backup_stops_the_migration()
    {
        Setup(known: ["001", "002"], applied: ["001"]);
        _backup.BackupAsync(default!, default!, default!, default)
            .ReturnsForAnyArgs(Task.FromException<string>(new BackupFailedException("no pg_dump")));

        await Should.ThrowAsync<BackupFailedException>(() => CreateMigrator().MigrateAsync());

        await _store.DidNotReceive().ApplyPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Database_newer_than_the_app_is_refused_without_a_backup_or_changes()
    {
        Setup(known: ["001"], applied: ["001", "002", "003"]);

        var exception = await Should.ThrowAsync<DatabaseNewerThanAppException>(() => CreateMigrator().MigrateAsync());

        exception.UnknownMigrations.ShouldBe(["002", "003"]);
        exception.Message.ShouldContain("newer version of Inkpact");
        exception.Message.ShouldContain("002, 003");
        await _store.DidNotReceive().ApplyPendingAsync(Arg.Any<CancellationToken>());
        await _backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Failed_migration_is_not_swallowed()
    {
        Setup(known: ["001"], applied: []);
        _store.ApplyPendingAsync(Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => CreateMigrator().MigrateAsync());
    }
}
