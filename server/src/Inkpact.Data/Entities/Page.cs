namespace Inkpact.Data.Entities;

/// <summary>
/// One node of the page tree. The title is metadata, not part of the Yjs document.
/// The tree lives here and is changed through the REST API, never through the CRDT.
/// </summary>
public class Page
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public Guid? ParentId { get; set; }

    public Page? Parent { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Fractional index key. Compared ordinally; siblings are never renumbered.</summary>
    public string SortKey { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set when the page is in the trash; null for live pages.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsTodo { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    /// <summary>Where an imported page came from, for example "joplin".</summary>
    public string? ExternalSourceType { get; set; }

    public string? ExternalId { get; set; }
}
