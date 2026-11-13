namespace Inkpact.Api.Health;

/// <summary>
/// The folders the app writes to, checked before it starts (run B.8): the Data Protection keys, the dumps before a
/// migration, attachments and uploaded imports, all under <c>/data</c> in the image. A folder the app cannot write stops it
/// at once, with one log line that names the folder and the command that fixes it. Without the check it would fail later
/// and less clearly, at the first key, dump or file. The usual cause is a host folder that belongs to root, as Unraid
/// makes the folder of a template's path; the image runs as UID 1654.
/// </summary>
public static class DataDirectories
{
    /// <summary>The user the image runs as (the base image's <c>app</c> user).</summary>
    public const int ImageUserId = 1654;

    /// <summary>The folders to check, with the setting that names each; the defaults are the app's own.</summary>
    public static IReadOnlyList<(string Setting, string Path)> Of(IConfiguration config)
    {
        static string Or(string? configured, string fallback) => configured is { Length: > 0 } ? configured : fallback;

        var folders = new List<(string, string)>
        {
            ("DataProtection:KeysDirectory", Or(config["DataProtection:KeysDirectory"], "data/keys")),
            ("Attachments:Directory", Or(config["Attachments:Directory"], "data/attachments")),
            ("Import:Directory", Or(config["Import:Directory"], "data/imports")),
        };

        // The dumps are only written when backing up before a migration is on (it is by default).
        if (!bool.TryParse(config["Database:BackupOnMigrate"], out var backup) || backup)
        {
            folders.Add(("Database:BackupDirectory", Or(config["Database:BackupDirectory"], "data/backups")));
        }

        return folders;
    }

    /// <summary>Whether every folder can be made and written to; logs the first that cannot, and stops there.</summary>
    public static bool Check(IConfiguration config, ILogger logger)
    {
        foreach (var (setting, path) in Of(config))
        {
            if (!CanWrite(path))
            {
                logger.LogCritical(
                    "Inkpact cannot write to {Folder} ({Setting}), so it does not start. The folder mounted there must belong to the user the app runs as: run chown -R {User}:{User} on it on the host (on Unraid, for example: chown -R {User}:{User} /mnt/user/appdata/inkpact), then start the app again.",
                    Path.GetFullPath(path), setting, ImageUserId, ImageUserId, ImageUserId, ImageUserId);
                return false;
            }
        }

        return true;
    }

    /// <summary>Makes the folder if needed, and writes and removes a small file in it.</summary>
    internal static bool CanWrite(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-check-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
