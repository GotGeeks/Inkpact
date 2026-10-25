namespace Inkpact.Data.Upgrade;

/// <summary>How to reach the database and where the dump goes.</summary>
public sealed record PgDumpRequest(
    string Host,
    int Port,
    string Database,
    string Username,
    string? Password,
    string OutputPath);

/// <summary>Runs pg_dump. A seam so tests can run it inside the database container.</summary>
public interface IPgDumpRunner
{
    /// <summary>Throws <see cref="BackupFailedException"/> when pg_dump is missing or fails.</summary>
    Task RunAsync(PgDumpRequest request, CancellationToken cancellationToken);
}
