using Npgsql;

namespace Inkpact.Data.Upgrade;

public class PgDumpBackup(IPgDumpRunner runner, TimeProvider timeProvider) : IDatabaseBackup
{
    public async Task<string> BackupAsync(
        string connectionString, string directory, string label, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var request = new PgDumpRequest(
            connection.Host ?? "localhost",
            connection.Port,
            connection.Database ?? throw new BackupFailedException("The connection string has no database name."),
            connection.Username ?? throw new BackupFailedException("The connection string has no user name."),
            connection.Password,
            OutputPath: "");

        Directory.CreateDirectory(directory);
        var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'");
        var path = Path.GetFullPath(Path.Combine(directory, $"inkpact-{timestamp}-before-{label}.dump"));

        try
        {
            await runner.RunAsync(request with { OutputPath = path }, cancellationToken);
        }
        catch
        {
            // A partial dump must not be mistaken for a good backup.
            File.Delete(path);
            throw;
        }

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new BackupFailedException($"pg_dump reported success but {path} is missing or empty.");
        }

        return path;
    }
}
