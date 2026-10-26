namespace Inkpact.Data.Entities;

/// <summary>
/// An update a compaction has folded into the snapshot. Kept for 30 days as a safety net (phase 1A) and for the
/// full-history check, then purged.
/// </summary>
public class ArchivedPageUpdate
{
    /// <summary>The sequence number the update had while live; not generated here.</summary>
    public long Seq { get; set; }

    public Guid PageId { get; set; }

    public byte[] Data { get; set; } = [];

    public Guid? UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ArchivedAt { get; set; }
}
