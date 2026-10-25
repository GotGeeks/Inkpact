namespace Inkpact.Data.Entities;

/// <summary>What a person may do in a workspace. A higher role includes everything the lower ones may do.</summary>
public enum WorkspaceRole
{
    /// <summary>No access at all. Never stored.</summary>
    None = 0,
    Viewer = 1,
    Editor = 2,
    Owner = 3,
}

public class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public WorkspaceRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
