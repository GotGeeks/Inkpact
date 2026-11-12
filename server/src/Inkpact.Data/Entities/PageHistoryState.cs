namespace Inkpact.Data.Entities;

/// <summary>
/// How far a page's history has got: which of its stored content is in a version, and what stopped the last attempt, if
/// anything did. One row per page that was ever looked at; a page without one has nothing captured yet. Nothing on the
/// edit path writes it: what is still to capture is worked out from the stored updates and this cursor (run B.5b).
/// </summary>
public class PageHistoryState
{
    public Guid PageId { get; set; }

    /// <summary>
    /// The page's last stored sequence number (the snapshot's watermark or its newest live update) when its newest version
    /// was captured. Content stored later has a higher one.
    /// </summary>
    public long CapturedSeq { get; set; }

    /// <summary>
    /// How many updates the page had been given when its newest version was captured: those in its snapshot and those
    /// still live. The page has something to capture when it has more now. The sequence number alone would miss an update
    /// that committed late, with a number below one that was already captured.
    /// </summary>
    public long CapturedUpdates { get; set; }

    /// <summary>How many attempts in a row failed. Zero after a capture.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>The page is left alone until then. Null when nothing failed.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>One of <see cref="PageHistoryFailures"/>, or null.</summary>
    public string? Failure { get; set; }
}

public static class PageHistoryFailures
{
    /// <summary>The page's stored content cannot be read back as the document it is: no version is made of it.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>The workspace's cap leaves no room, also after the oldest unprotected versions went.</summary>
    public const string Full = "full";
}

/// <summary>
/// A title a page was given, kept until the capture worker has made a version with it. Written in the rename's own
/// transaction, which costs one insert and no decoding, so two renames in a minute leave two versions (run B.5b).
/// </summary>
public class PageTitleChange
{
    public long Id { get; set; }

    public Guid PageId { get; set; }

    public string Title { get; set; } = "";

    /// <summary>The page's metadata version right after this rename: a capture takes the changes up to the version it observed.</summary>
    public int PageVersion { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>Who renamed it. Cleared when the user is deleted.</summary>
    public Guid? ChangedBy { get; set; }
}
