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
}

public static class AuditEventTypes
{
    public const string SetupCompleted = "setup_completed";
    public const string LoginSucceeded = "login_succeeded";
    public const string LoginFailed = "login_failed";
    public const string LoginLocked = "login_locked";
    public const string Logout = "logout";
    public const string LogoutEverywhere = "logout_everywhere";
}
