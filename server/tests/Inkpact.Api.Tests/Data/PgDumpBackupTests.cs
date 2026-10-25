using Inkpact.Data.Upgrade;
using NSubstitute;

namespace Inkpact.Api.Tests.Data;

public sealed class PgDumpBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"inkpact-backup-{Guid.NewGuid():N}");
    private readonly IPgDumpRunner _runner = Substitute.For<IPgDumpRunner>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 11, 30, 5, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private PgDumpBackup CreateBackup() => new(_runner, _time);

    [Fact]
    public async Task Names_the_file_after_the_time_and_the_last_applied_migration()
    {
        _runner.RunAsync(Arg.Any<PgDumpRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllText(call.Arg<PgDumpRequest>().OutputPath, "dump");
            return Task.CompletedTask;
        });

        var path = await CreateBackup().BackupAsync(
            "Host=db.example;Port=5433;Database=inkpact;Username=ink;Password=secret", _directory, "20261025_Init", default);

        Path.GetFileName(path).ShouldBe("inkpact-20261025T113005Z-before-20261025_Init.dump");
        Path.GetDirectoryName(path).ShouldBe(Path.GetFullPath(_directory));
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task Passes_the_connection_details_to_the_runner()
    {
        PgDumpRequest? seen = null;
        _runner.RunAsync(Arg.Any<PgDumpRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seen = call.Arg<PgDumpRequest>();
            File.WriteAllText(seen.OutputPath, "dump");
            return Task.CompletedTask;
        });

        await CreateBackup().BackupAsync(
            "Host=db.example;Port=5433;Database=inkpact;Username=ink;Password=secret", _directory, "x", default);

        seen.ShouldNotBeNull();
        seen.Host.ShouldBe("db.example");
        seen.Port.ShouldBe(5433);
        seen.Database.ShouldBe("inkpact");
        seen.Username.ShouldBe("ink");
        seen.Password.ShouldBe("secret");
    }

    [Fact]
    public async Task A_failing_runner_leaves_no_partial_file()
    {
        _runner.RunAsync(Arg.Any<PgDumpRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllText(call.Arg<PgDumpRequest>().OutputPath, "half");
            return Task.FromException(new BackupFailedException("disk full"));
        });

        await Should.ThrowAsync<BackupFailedException>(() => CreateBackup().BackupAsync(
            "Host=db;Database=inkpact;Username=ink", _directory, "x", default));

        Directory.GetFiles(_directory).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_dump_is_a_failure()
    {
        _runner.RunAsync(Arg.Any<PgDumpRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            File.WriteAllBytes(call.Arg<PgDumpRequest>().OutputPath, []);
            return Task.CompletedTask;
        });

        await Should.ThrowAsync<BackupFailedException>(() => CreateBackup().BackupAsync(
            "Host=db;Database=inkpact;Username=ink", _directory, "x", default));
    }

    [Fact]
    public async Task A_runner_that_writes_nothing_is_a_failure()
    {
        await Should.ThrowAsync<BackupFailedException>(() => CreateBackup().BackupAsync(
            "Host=db;Database=inkpact;Username=ink", _directory, "x", default));
    }

    [Theory]
    [InlineData("Host=db;Username=ink")]
    [InlineData("Host=db;Database=inkpact")]
    public async Task A_connection_string_without_database_or_user_is_a_failure(string connectionString)
    {
        await Should.ThrowAsync<BackupFailedException>(() => CreateBackup().BackupAsync(connectionString, _directory, "x", default));

        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }

    [Fact]
    public void Pg_dump_arguments_never_contain_the_password()
    {
        var arguments = ProcessPgDumpRunner.BuildArguments(
            new PgDumpRequest("db", 5432, "inkpact", "ink", "secret", "/data/x.dump"));

        arguments.ShouldBe(
        [
            "--format=custom",
            "--no-password",
            "--host=db",
            "--port=5432",
            "--username=ink",
            "--dbname=inkpact",
            "--file=/data/x.dump",
        ]);
        arguments.ShouldNotContain(a => a.Contains("secret"));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
