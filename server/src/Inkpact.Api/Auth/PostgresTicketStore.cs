using System.Security.Claims;
using System.Security.Cryptography;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Inkpact.Api.Auth;

/// <summary>
/// Keeps the signed-in principal in Postgres instead of in the cookie. The cookie then holds only a session id,
/// so logging out everywhere, revoking one session and removing a user all take effect on the next request.
/// </summary>
public sealed class PostgresTicketStore(IDbContextFactory<InkpactDbContext> dbFactory, TimeProvider time, SessionEvents sessionEvents) : ITicketStore
{
    /// <summary>Sessions are touched at most this often, so reading a page does not write a row.</summary>
    /// <summary>Key in the ticket's properties for the browser's user agent, set at sign-in and kept with the session.</summary>
    public const string UserAgentItem = "ua";

    /// <summary>
    /// Key in the properties of a retrieved ticket for the session it came from, so that a request can tell which session
    /// it belongs to (the sync hub closes a session's sockets when it ends).
    /// </summary>
    public const string SessionKeyItem = "session";

    internal static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var userId = GetUserId(ticket.Principal);
        var now = time.GetUtcNow();
        var session = new Session
        {
            Id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            UserId = userId,
            Ticket = TicketSerializer.Default.Serialize(ticket),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = ticket.Properties.ExpiresUtc ?? now.AddDays(30),
            UserAgent = ticket.Properties.Items.TryGetValue(UserAgentItem, out var userAgent) ? userAgent : null,
        };

        await using var db = await dbFactory.CreateDbContextAsync();
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Sessions.Where(s => s.Id == key).ExecuteUpdateAsync(set => set
            .SetProperty(s => s.Ticket, TicketSerializer.Default.Serialize(ticket))
            .SetProperty(s => s.ExpiresAt, ticket.Properties.ExpiresUtc ?? now.AddDays(30))
            .SetProperty(s => s.LastSeenAt, now));
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        if (session is null || session.ExpiresAt <= now)
        {
            return null;
        }

        if (now - session.LastSeenAt >= LastSeenResolution)
        {
            await db.Sessions.Where(s => s.Id == key).ExecuteUpdateAsync(set => set.SetProperty(s => s.LastSeenAt, now));
        }

        var ticket = TicketSerializer.Default.Deserialize(session.Ticket);
        if (ticket is not null)
        {
            ticket.Properties.Items[SessionKeyItem] = key;
        }

        return ticket;
    }

    public async Task RemoveAsync(string key)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Sessions.Where(s => s.Id == key).ExecuteDeleteAsync();
        sessionEvents.SessionEnded(key);
    }

    /// <summary>Whether the session still exists and has not expired. Unlike <see cref="RetrieveAsync"/> it changes nothing.</summary>
    public async Task<bool> IsActiveAsync(string key, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sessions.AnyAsync(s => s.Id == key && s.ExpiresAt > now, cancellationToken);
    }

    /// <summary>
    /// What a person sees of a session (run B.9): 16 bytes of SHA-256 over its key, base64url. The key itself is what the
    /// cookie holds, and would sign in whoever read it; the handle cannot be turned back into it.
    /// </summary>
    public static string HandleOf(string key) => WebEncoders.Base64UrlEncode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));

    /// <summary>
    /// A user's sessions that have not expired: the one named by <paramref name="currentKey"/> first, then the most recently
    /// seen, at most <paramref name="limit"/>; and how many there are in all.
    /// </summary>
    public async Task<(IReadOnlyList<Session> Sessions, int Total)> ListForUserAsync(
        Guid userId, string currentKey, int limit, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var live = db.Sessions.AsNoTracking().Where(s => s.UserId == userId && s.ExpiresAt > now);
        var total = await live.CountAsync(cancellationToken);
        var sessions = await live
            .OrderByDescending(s => s.Id == currentKey)
            .ThenByDescending(s => s.LastSeenAt)
            .ThenByDescending(s => s.CreatedAt)
            .Take(limit)
            .Select(s => new Session { Id = s.Id, UserId = s.UserId, CreatedAt = s.CreatedAt, LastSeenAt = s.LastSeenAt, ExpiresAt = s.ExpiresAt, UserAgent = s.UserAgent })
            .ToListAsync(cancellationToken);
        return (sessions, total);
    }

    /// <summary>
    /// The key of the user's session with this handle, or null: someone else's handle, or one that does not exist, is the
    /// same answer. The user's keys are few, so they are compared here rather than storing the handle.
    /// </summary>
    public async Task<string?> FindForUserAsync(Guid userId, string handle, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var keys = await db.Sessions.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync(cancellationToken);
        return keys.FirstOrDefault(key => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(HandleOf(key)), System.Text.Encoding.ASCII.GetBytes(handle)));
    }

    /// <summary>
    /// Signs a user out of every browser but the one with <paramref name="keepKey"/>, in one statement (so a session made
    /// meanwhile goes too), and closes their sync sockets. Returns how many sessions ended.
    /// </summary>
    public async Task<int> RemoveAllForUserExceptAsync(Guid userId, string keepKey, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var removed = await db.Sessions.Where(s => s.UserId == userId && s.Id != keepKey).ExecuteDeleteAsync(cancellationToken);
        sessionEvents.AllSessionsEndedExcept(userId, keepKey);
        return removed;
    }

    /// <summary>Signs a user out of every browser. Returns how many sessions ended.</summary>
    public async Task<int> RemoveAllForUserAsync(Guid userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var removed = await db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
        sessionEvents.AllSessionsEnded(userId);
        return removed;
    }

    public async Task<int> DeleteExpiredAsync()
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Sessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync();
    }

    private static Guid GetUserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("The ticket has no user id."));
}

/// <summary>Removes expired sessions once an hour. An expired session is already refused when read; this keeps the table small.</summary>
public sealed class SessionCleanupWorker(PostgresTicketStore store, ILogger<SessionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var removed = await store.DeleteExpiredAsync();
                if (removed > 0)
                {
                    logger.LogInformation("Removed {Count} expired session(s)", removed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Removing expired sessions failed; trying again in an hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
