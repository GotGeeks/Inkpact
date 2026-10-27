namespace Inkpact.Data.Entities;

/// <summary>
/// A label of a workspace. Names are unique per workspace regardless of case. Tags come from the Joplin import for now;
/// the interface that shows them is phase 1B.
/// </summary>
public class Tag
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid WorkspaceId { get; set; }

    /// <summary>The name as written.</summary>
    public string Name { get; set; } = "";

    /// <summary>The name in lower case, which is what has to be unique.</summary>
    public string NormalizedName { get; set; } = "";

    /// <summary>Where an imported tag came from, for example "joplin".</summary>
    public string? ExternalSourceType { get; set; }

    public string? ExternalId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A tag on a page.</summary>
public class PageTag
{
    public Guid PageId { get; set; }

    public Guid TagId { get; set; }
}
