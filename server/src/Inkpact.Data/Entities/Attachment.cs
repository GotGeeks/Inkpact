namespace Inkpact.Data.Entities;

/// <summary>
/// A file added to a page. One row per upload; the bytes are stored once per content, under their SHA-256, by the
/// attachment store, not in Postgres. Page content refers to an attachment by this id, never by an address.
/// </summary>
public class Attachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The page it was added to. Who may read the page may read the file.</summary>
    public Guid PageId { get; set; }

    /// <summary>The name it was uploaded with, cleaned so it is safe in a download header and on any file system.</summary>
    public string FileName { get; set; } = "";

    /// <summary>The type the server worked out, never the one the client claimed.</summary>
    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    /// <summary>Lower-case hex of the content's SHA-256: the key of the stored file.</summary>
    public string Sha256 { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who uploaded it. Kept when the user is removed, without the link.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>Where an imported file came from, for example "joplin". Null for an upload.</summary>
    public string? ExternalSourceType { get; set; }

    /// <summary>The file's id in the other app. A file used by several pages has a row on each, with the same id.</summary>
    public string? ExternalId { get; set; }
}
