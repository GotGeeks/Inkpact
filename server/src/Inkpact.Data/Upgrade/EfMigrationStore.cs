using Microsoft.EntityFrameworkCore;

namespace Inkpact.Data.Upgrade;

public class EfMigrationStore(InkpactDbContext db) : IMigrationStore
{
    public string ConnectionString => db.Database.GetConnectionString()!;

    public IReadOnlyList<string> GetKnownMigrations() => db.Database.GetMigrations().ToList();

    public async Task<IReadOnlyList<string>> GetAppliedMigrationsAsync(CancellationToken cancellationToken) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList()
            : [];

    public Task ApplyPendingAsync(CancellationToken cancellationToken) => db.Database.MigrateAsync(cancellationToken);
}
