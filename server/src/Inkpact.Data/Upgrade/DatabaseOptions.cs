namespace Inkpact.Data.Upgrade;

public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Write a pg_dump to <see cref="BackupDirectory"/> before applying migrations to an existing database.</summary>
    public bool BackupOnMigrate { get; set; } = true;

    /// <summary>Folder on the data volume that receives the pre-migration dumps.</summary>
    public string BackupDirectory { get; set; } = "data/backups";
}
