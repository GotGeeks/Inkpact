namespace Inkpact.Api.Import;

/// <summary>Where uploaded export files wait for the import job, and what they may be, from the <c>Import</c> configuration section.</summary>
public sealed class ImportOptions
{
    public const string SectionName = "Import";

    /// <summary>Where an uploaded file is kept until the job has read it. The image points it at the data volume.</summary>
    public string Directory { get; set; } = "data/imports";

    /// <summary>The largest export file one upload may be, in bytes. A person's whole library is in it, so this is far above the limit of a single attachment.</summary>
    public long MaxFileSize { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How long the job waits for the Yjs sidecar to come back before it gives up on the import.</summary>
    public TimeSpan SidecarWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the job waits between two looks at the sidecar while it is down.</summary>
    public TimeSpan SidecarRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How many items in a row may fail for a reason that is not the item's (a database that is gone, say) before the job gives up.</summary>
    public int MaxConsecutiveFailures { get; set; } = 10;
}
