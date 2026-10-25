namespace Inkpact.Data.Entities;

/// <summary>
/// A signed-in browser. The cookie only carries the id; the ticket (the signed-in principal) lives here,
/// so deleting the row signs that browser out at once.
/// </summary>
public class Session
{
    /// <summary>32 random bytes, base64url. The key the cookie points to.</summary>
    public string Id { get; set; } = "";

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The serialized authentication ticket.</summary>
    public byte[] Ticket { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? UserAgent { get; set; }
}
