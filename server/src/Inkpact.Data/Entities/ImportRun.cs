namespace Inkpact.Data.Entities;

/// <summary>The states of an import, as stored.</summary>
public static class ImportStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>
/// One import of a file from another app, from the upload to the report. The file is read by a background job and deleted
/// when the job ends; what stays is this row, with the counts and the report as JSON.
/// </summary>
public class ImportRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid WorkspaceId { get; set; }

    /// <summary>Who started it. Only they can read its progress and report.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Where the file came from, for example "joplin".</summary>
    public string SourceType { get; set; } = "";

    /// <summary>The name the file was uploaded with, for the person's own overview.</summary>
    public string FileName { get; set; } = "";

    public long FileSize { get; set; }

    /// <summary>One of <see cref="ImportStates"/>.</summary>
    public string State { get; set; } = ImportStates.Queued;

    /// <summary>Why a failed import failed: a code the client translates. Null otherwise.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>How many items (notebooks, notes, tags) the file holds that the job goes through; known once it has read the file.</summary>
    public int TotalItems { get; set; }

    /// <summary>How many of them it has dealt with: imported, skipped or failed.</summary>
    public int ProcessedItems { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The counts per kind, as JSON (<c>jsonb</c>). Written as the job goes, and final at the end.</summary>
    public string Counts { get; set; } = "{}";

    /// <summary>The report's list of items that were skipped or converted imperfectly, as JSON (<c>jsonb</c>). Final at the end.</summary>
    public string Report { get; set; } = "[]";
}

/// <summary>
/// What an import made of one item of the other app: the page, tag or notebook it became, a hash of the content as it was
/// imported, and for a page the state of its Yjs document right after. That is what a later import needs to tell
/// whether the item changed in the other app (the hash differs) and whether the page changed here (it has gone past the state
/// vector), without keeping the old content (run 1.16). It is also how an item that was imported is recognised.
/// </summary>
public class ImportItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid WorkspaceId { get; set; }

    /// <summary>For example "joplin".</summary>
    public string SourceType { get; set; } = "";

    /// <summary>The item's id in the other app.</summary>
    public string ExternalId { get; set; } = "";

    /// <summary>"notebook", "note" or "tag".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The id of the page (notebook or note) or tag it became.</summary>
    public Guid InkpactId { get; set; }

    /// <summary>SHA-256, as lower-case hex, of the item's content as it was imported.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>The page's Yjs state vector right after the import. Null for tags and for notebooks.</summary>
    public byte[]? StateVector { get; set; }

    /// <summary>The import that made it.</summary>
    public Guid ImportId { get; set; }

    public DateTimeOffset ImportedAt { get; set; }
}
