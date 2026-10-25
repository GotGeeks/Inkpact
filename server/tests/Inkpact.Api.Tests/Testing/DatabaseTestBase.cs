using Inkpact.Data;
using Inkpact.Data.Upgrade;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// Base class for integration tests that need the real schema: a fresh, fully migrated database per test.
/// </summary>
[Collection(PostgresCollection.Name)]
public abstract class DatabaseTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; } = postgres;

    protected string ConnectionString { get; private set; } = "";

    public virtual async Task InitializeAsync()
    {
        ConnectionString = await Postgres.CreateDatabaseAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected InkpactDbContext CreateContext() => CreateContext(ConnectionString);

    protected static InkpactDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);

    protected static EfMigrationStore CreateStore(InkpactDbContext db) => new(db);
}
