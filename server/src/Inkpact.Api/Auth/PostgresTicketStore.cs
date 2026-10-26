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
