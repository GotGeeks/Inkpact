namespace Inkpact.Data.Upgrade;

/// <summary>The database has migrations this version of Inkpact does not know: it was written by a newer version.</summary>
public class DatabaseNewerThanAppException(IReadOnlyList<string> unknownMigrations)
    : Exception(
        "The database was migrated by a newer version of Inkpact and this version cannot use it "
        + $"(unknown migrations: {string.Join(", ", unknownMigrations)}). "
        + "Upgrade Inkpact, or restore the backup that was taken before the upgrade. Downgrades are not supported.")
{
    public IReadOnlyList<string> UnknownMigrations { get; } = unknownMigrations;
}
