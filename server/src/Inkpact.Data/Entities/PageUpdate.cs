namespace Inkpact.Data.Entities;

/// <summary>
/// One Yjs update of a page's content, appended by the sync hub as it arrived. The bytes are opaque to the server;
/// only compaction (run 1.10) decodes them, through the Yjs sidecar.
/// </summary>
public class PageUpdate
{
    /// <summary>Increases with every append, across all pages. Compaction uses it as its watermark.</summary>
    public long Seq { get; set; }

    public Guid PageId { get; set; }

    public byte[] Data { get; set; } = [];

    /// <summary>Who sent it. Kept when the user is removed, without the link.</summary>
    public Guid? UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
