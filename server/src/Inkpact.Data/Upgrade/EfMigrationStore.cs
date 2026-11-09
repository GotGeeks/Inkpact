using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inkpact.Data.Upgrade;

/// <summary>
/// The migrations, through EF. A fresh install would work with EF's own calls alone, but they log two errors on the way: one
/// for opening a database that does not exist yet (the app makes it), and one for reading a migration history table that
/// does not exist yet (Npgsql finds out by reading it). A self-hoster's first log would then point at a fault that is not
/// there, so both are made before they are read.
/// </summary>
public class EfMigrationStore(InkpactDbContext db) : IMigrationStore
{
    public string ConnectionString => db.Database.GetConnectionString()!;

    public IReadOnlyList<string> GetKnownMigrations() => db.Database.GetMigrations().ToList();

    /// <summary>
    /// The migrations the database has; none when there is no database yet. An empty database (as the Postgres container of
    /// docker-compose.yml makes one) gets the empty history table here, as Migrate would give it, so it can be read.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAppliedMigrationsAsync(CancellationToken cancellationToken)
    {
        // CanConnect expects a "no" and logs it at debug.
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return [];
        }

        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
        return (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
    }

    public async Task ApplyPendingAsync(CancellationToken cancellationToken)
    {
        // Migrate makes a missing database too, but tries to open it first and logs that as an error. Exists expects a "no".
        // A database Migrate did not make itself is then read for its history before Migrate makes the table, so that
        // is made here as well.
        var creator = db.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken))
        {
            await creator.CreateAsync(cancellationToken);
        }

        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }
}
