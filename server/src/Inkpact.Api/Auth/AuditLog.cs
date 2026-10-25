using Inkpact.Data;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Auth;

/// <summary>
/// Writes security events to their own table. Each write is awaited and a failing one fails the request, so the
/// log has no silent gaps. There is no way to read it yet (admin page, run 2.9).
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(HttpContext http, string type, Guid? userId = null, string? email = null, CancellationToken cancellationToken = default);
}

public sealed class AuditLog(InkpactDbContext db, TimeProvider time) : IAuditLog
{
    public async Task WriteAsync(HttpContext http, string type, Guid? userId = null, string? email = null, CancellationToken cancellationToken = default)
    {
        db.AuditEvents.Add(Create(http, time, type, userId, email));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Builds the row without saving it, for events that belong in a transaction that is already open.</summary>
    internal static AuditEvent Create(HttpContext http, TimeProvider time, string type, Guid? userId, string? email)
    {
        var userAgent = http.Request.Headers.UserAgent.ToString();
        return new AuditEvent
        {
            OccurredAt = time.GetUtcNow(),
            Type = type,
            UserId = userId,
            Email = email is { Length: > 320 } ? email[..320] : email,
            Ip = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length == 0 ? null : userAgent.Length > 256 ? userAgent[..256] : userAgent,
        };
    }
}
