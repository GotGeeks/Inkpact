namespace Inkpact.Data.Upgrade;

public interface IDatabaseBackup
{
    /// <summary>Writes a dump of the database and returns the path of the file. Throws <see cref="BackupFailedException"/>.</summary>
    Task<string> BackupAsync(string connectionString, string directory, string label, CancellationToken cancellationToken);
}
