using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inkpact.Data.Upgrade;

/// <summary>
/// Applies pending migrations at startup. Refuses a database that is newer than the app, and
/// takes a pg_dump first when an existing database is about to change.
/// </summary>
public class DatabaseMigrator(
    IMigrationStore store,
    IDatabaseBackup backup,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        var known = store.GetKnownMigrations();
        var applied = await store.GetAppliedMigrationsAsync(cancellationToken);

        var unknown = applied.Except(known).ToList();
        if (unknown.Count > 0)
        {
            throw new DatabaseNewerThanAppException(unknown);
        }

        var pending = known.Except(applied).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database is up to date at {Migration}", applied.Count > 0 ? applied[^1] : null);
            return;
        }

        // A new database has nothing to lose, so only an existing one is dumped.
        if (applied.Count > 0 && options.Value.BackupOnMigrate)
        {
            var path = await backup.BackupAsync(
                store.ConnectionString, options.Value.BackupDirectory, applied[^1], cancellationToken);
            logger.LogInformation("Wrote a backup to {Path} before applying {Count} migration(s)", path, pending.Count);
        }

        logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await store.ApplyPendingAsync(cancellationToken);
    }
}
