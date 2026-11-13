using Inkpact.Api.Health;
using Inkpact.Api.Tests.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Inkpact.Api.Tests.Health;

/// <summary>
/// The check of the folders the app writes to, before it starts (run B.8). A folder it cannot write is made here by a path
/// below a file, which no system lets become a folder.
/// </summary>
public sealed class DataDirectoriesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inkpact-data-{Guid.NewGuid():N}");
    private readonly CapturingLogger<DataDirectoriesTests> _logger = new();

    public DataDirectoriesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IConfiguration Config(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private Dictionary<string, string?> Folders(bool backup = true) => new()
    {
        ["DataProtection:KeysDirectory"] = Path.Combine(_root, "keys"),
        ["Attachments:Directory"] = Path.Combine(_root, "attachments"),
        ["Import:Directory"] = Path.Combine(_root, "imports"),
        ["Database:BackupDirectory"] = Path.Combine(_root, "backups"),
        ["Database:BackupOnMigrate"] = backup.ToString(),
    };

    /// <summary>A path the app cannot make a folder of.</summary>
    private string Unwritable()
    {
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "not a folder");
        return Path.Combine(file, "below");
    }

    [Fact]
    public void Passes_when_every_folder_can_be_written_and_leaves_nothing_behind()
    {
        DataDirectories.Check(Config(Folders()), _logger).ShouldBeTrue();

        _logger.Entries.ShouldBeEmpty();
        Directory.GetDirectories(_root).Select(Path.GetFileName).Order().ShouldBe(["attachments", "backups", "imports", "keys"]);
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DataProtection:KeysDirectory")]
    [InlineData("Attachments:Directory")]
    [InlineData("Import:Directory")]
    [InlineData("Database:BackupDirectory")]
    public void Stops_at_a_folder_it_cannot_write_and_says_what_to_run(string setting)
    {
        var settings = Folders();
        var unwritable = Unwritable();
        settings[setting] = unwritable;

        DataDirectories.Check(Config(settings), _logger).ShouldBeFalse();

        var (level, message) = _logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Critical);
        message.ShouldContain(Path.GetFullPath(unwritable));
        message.ShouldContain($"({setting})");
        message.ShouldContain("chown -R 1654:1654");
    }

    [Fact]
    public void Leaves_the_dump_folder_alone_when_no_dump_is_made()
    {
        var settings = Folders(backup: false);
        settings["Database:BackupDirectory"] = Unwritable();

        DataDirectories.Check(Config(settings), _logger).ShouldBeTrue();
    }

    [Fact]
    public void Checks_the_app_s_own_folders_when_none_are_set()
    {
        DataDirectories.Of(Config([])).ShouldBe(
        [
            ("DataProtection:KeysDirectory", "data/keys"),
            ("Attachments:Directory", "data/attachments"),
            ("Import:Directory", "data/imports"),
            ("Database:BackupDirectory", "data/backups"),
        ]);
    }
}
