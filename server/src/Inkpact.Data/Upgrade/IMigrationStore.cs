namespace Inkpact.Data.Upgrade;

/// <summary>The database's migration state, as the migrator needs to see it.</summary>
public interface IMigrationStore
{
    string ConnectionString { get; }

    /// <summary>Migrations compiled into this version of the app, oldest first.</summary>
    IReadOnlyList<string> GetKnownMigrations();

    /// <summary>Migrations recorded in the database, oldest first. Empty when the database does not exist yet.</summary>
    Task<IReadOnlyList<string>> GetAppliedMigrationsAsync(CancellationToken cancellationToken);

    /// <summary>Creates the database if needed and applies every pending migration.</summary>
    Task ApplyPendingAsync(CancellationToken cancellationToken);
}
