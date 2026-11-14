namespace Inkpact.Data.Entities;

/// <summary>One security-relevant thing that happened. Written when it happens, never changed afterwards.</summary>
public class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>One of <see cref="AuditEventTypes"/>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Who it was about. Cleared when the user is deleted; the row stays.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The address that was typed, for logins that did not succeed.</summary>
    public string? Email { get; set; }

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>The workspace it concerned, for events about one (an export). Kept when the workspace is deleted, without the link.</summary>
    public Guid? WorkspaceId { get; set; }
}

public static class AuditEventTypes
{
    public const string SetupCompleted = "setup_completed";
    public const string LoginSucceeded = "login_succeeded";
    public const string LoginFailed = "login_failed";
    public const string LoginLocked = "login_locked";
    public const string Logout = "logout";
    public const string LogoutEverywhere = "logout_everywhere";

    /// <summary>A new password was set from the command line (<c>reset-password</c>). No address: it came from the console.</summary>
    public const string PasswordReset = "password_reset";

    /// <summary>A person signed one of their other devices out, from the settings screen (run B.9).</summary>
    public const string SessionRevoked = "session_revoked";

    /// <summary>A person signed out every device but the one they used, from the settings screen (run B.9).</summary>
    public const string SessionsRevoked = "sessions_revoked";

    /// <summary>Someone took the export of a workspace: all of its pages and files.</summary>
    public const string WorkspaceExported = "workspace_exported";
}
