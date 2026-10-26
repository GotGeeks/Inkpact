namespace Inkpact.Data.Entities;

/// <summary>
/// The compacted Yjs state of a page: its updates up to <see cref="Watermark"/> folded into one. A page's content is this
/// snapshot plus the updates still in <c>page_updates</c>. One row per page, written only by the compactor.
/// </summary>
public class PageSnapshot
{
    public Guid PageId { get; set; }

    public byte[] State { get; set; } = [];

    public byte[] StateVector { get; set; } = [];

    /// <summary>The highest update sequence number this snapshot contains. Never moves back.</summary>
    public long Watermark { get; set; }

    /// <summary>+1 per compaction.</summary>
    public int Version { get; set; }

    /// <summary>All updates ever folded into this snapshot.</summary>
    public long UpdateCount { get; set; }

    /// <summary>
    /// False once the purge has removed any archived update of this page: the full-history check needs every update, so it
    /// stops for this page.
    /// </summary>
    public bool HistoryComplete { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}
