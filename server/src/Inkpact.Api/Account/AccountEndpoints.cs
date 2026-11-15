using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Account;

public sealed record AccountUpdate(string? DisplayName);

/// <summary>
/// One signed-in browser as its owner sees it (run B.9). <paramref name="Id"/> is the session's handle, never its key:
/// see <see cref="PostgresTicketStore.HandleOf"/>.
/// </summary>
public sealed record SessionSummary(string Id, DateTimeOffset SignedInAt, DateTimeOffset LastSeenAt, string? UserAgent, bool Current);

/// <summary>The sessions shown, and how many more there are beyond them.</summary>
public sealed record SessionList(IReadOnlyList<SessionSummary> Sessions, int More);

public sealed record SessionsEnded(int Ended);

public sealed record PasswordChange(string? CurrentPassword, string? NewPassword);

/// <summary>
/// A person's own account (run B.9): their display name, the browsers they are signed in on, and their password. Only ever
/// the signed-in person's: no endpoint here takes a user id.
/// </summary>
public static class AccountEndpoints
{
    /// <summary>How many sessions the list shows; the rest are counted in <see cref="SessionList.More"/>.</summary>
    public const int MaxListed = 100;

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").RequireAuthorization(Policies.SignedIn);
        group.MapPatch("", Update);
        group.MapGet("/sessions", ListSessions);
        group.MapDelete("/sessions/{id}", EndSession);
        group.MapPost("/sessions/sign-out-others", EndOtherSessions);
        // The same per-address limit as login and setup: a stolen session must not be a faster way to guess the password.
        group.MapPost("/password", ChangePassword).RequireRateLimiting(ThrottleOptions.RateLimitPolicy);
        return app;
    }

    private static async Task<IResult> Update(AccountUpdate request, HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var displayName = request.DisplayName?.Trim() ?? "";
        if (displayName.Length is 0 or > AuthEndpoints.MaxNameLength)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "display_name_invalid", "displayName");
        }

        var userId = http.UserId();
        // Removed while the request was on its way (an administrator, from phase 2): signed out, not a server error.
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotSignedIn();
        }

        user.DisplayName = displayName;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new CurrentUser(user.Id, user.Email, user.DisplayName, user.IsAdmin));
    }

    private static async Task<IResult> ListSessions(HttpContext http, PostgresTicketStore store, CancellationToken cancellationToken)
    {
        var current = await http.SessionKeyAsync();
        if (current is null)
        {
            return NotSignedIn();
        }

        var (sessions, total) = await store.ListForUserAsync(http.UserId(), current, MaxListed, cancellationToken);
        var summaries = sessions
            .Select(s => new SessionSummary(PostgresTicketStore.HandleOf(s.Id), s.CreatedAt, s.LastSeenAt, s.UserAgent, s.Id == current))
            .ToList();
        return Results.Ok(new SessionList(summaries, Math.Max(0, total - summaries.Count)));
    }

    /// <summary>
    /// Signs one of the person's other devices out; its sync sockets close at once. The current device is refused: the
    /// client signs it out through the menu, which also clears what it kept for offline use.
    /// </summary>
    private static async Task<IResult> EndSession(
        string id, HttpContext http, PostgresTicketStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var current = await http.SessionKeyAsync();
        if (current is null)
        {
            return NotSignedIn();
        }

        var userId = http.UserId();
        var key = await store.FindForUserAsync(userId, id, current, MaxListed, cancellationToken);
        if (key is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "session_not_found");
        }

        if (key == current)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "session_is_current");
        }

        await audit.WriteAsync(http, AuditEventTypes.SessionRevoked, userId, cancellationToken: cancellationToken);
        await store.RemoveAsync(key);
        return Results.NoContent();
    }

    private static async Task<IResult> EndOtherSessions(
        HttpContext http, PostgresTicketStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var current = await http.SessionKeyAsync();
        if (current is null)
        {
            return NotSignedIn();
        }

        var userId = http.UserId();
        await audit.WriteAsync(http, AuditEventTypes.SessionsRevoked, userId, cancellationToken: cancellationToken);
        var ended = await store.RemoveAllForUserExceptAsync(userId, current, cancellationToken);
        return Results.Ok(new SessionsEnded(ended));
    }

    /// <summary>
    /// A new password, with the current one asked (run B.9, decision 4). Guarded as a login is
    /// (<see cref="AuthEndpoints.CheckPasswordAsync"/>): a wrong current password counts toward the lockout of the person's
    /// email, and while it is locked nothing is checked. In one transaction the hash is replaced, every other session of the
    /// person removed, the lockout cleared and the event written; then the other devices' sync sockets close. This device
    /// stays signed in: its ticket holds only the user id.
    /// </summary>
    private static async Task<IResult> ChangePassword(
        PasswordChange request,
        HttpContext http,
        InkpactDbContext db,
        IPasswordHasher hasher,
        LoginThrottling throttle,
        SessionEvents sessionEvents,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var current = await http.SessionKeyAsync();
        if (current is null)
        {
            return NotSignedIn();
        }

        var userId = http.UserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return NotSignedIn();
        }

        var currentPassword = request.CurrentPassword ?? "";
        var newPassword = request.NewPassword ?? "";

        // The new password's rules first: they tell nothing about the current one and need no hash, so a new password that
        // is too short is not counted as a guess.
        if (PasswordPolicy.Validate(newPassword, user.Email) is { } policyError)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, policyError, "newPassword");
        }

        // The login's guard and counter for this address, so a lock here is a lock there, and the other way round.
        var throttleKey = LoginThrottling.KeyFor(user.Email);
        var check = await AuthEndpoints.CheckPasswordAsync(throttle, hasher, throttleKey, user.PasswordHash, currentPassword, cancellationToken);
        if (check.Outcome == AuthEndpoints.PasswordCheckOutcome.Locked)
        {
            await audit.WriteAsync(http, AuditEventTypes.LoginLocked, userId, cancellationToken: cancellationToken);
            http.Response.Headers.RetryAfter = AuthEndpoints.RetryAfterSeconds(check.LockedFor);
            return ApiResults.Error(StatusCodes.Status429TooManyRequests, "login_locked");
        }

        if (check.Outcome == AuthEndpoints.PasswordCheckOutcome.Wrong)
        {
            await audit.WriteAsync(http, AuditEventTypes.PasswordChangeFailed, userId, cancellationToken: cancellationToken);
            // 400, not 401: a 401 means "not signed in", and this person still is.
            return CurrentPasswordInvalid();
        }

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
        {
            // A change that changes nothing would only sign the other devices out.
            return ApiResults.Error(StatusCodes.Status400BadRequest, "password_unchanged", "newPassword");
        }

        // The slow part happens before the transaction opens.
        var oldHash = user.PasswordHash!;
        var newHash = hasher.Hash(newPassword);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // First, and only over the hash that was checked: the row is locked from here, so a login that checked the old
            // password either finds it changed or has stored its session before the sessions are removed below. Of two
            // changes at once, the second finds a different hash and changes nothing.
            var expected = oldHash;
            while (await db.Users
                .Where(u => u.Id == userId && u.PasswordHash == expected)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, newHash), cancellationToken) == 0)
            {
                // A different hash. A login may have only upgraded it (same password, new parameters): then the password
                // typed is still the current one, and the change is tried once more over that hash. Otherwise it was
                // changed meanwhile, and the password typed is no longer the current one.
                var stored = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleOrDefaultAsync(cancellationToken);
                if (expected != oldHash || stored is null || !hasher.Verify(stored, currentPassword))
                {
                    return CurrentPasswordInvalid();
                }

                expected = stored;
            }

            // This device was already signed out (from another one): change nothing, or the person would be left with no
            // session anywhere. Two devices that sign each other out at the very same moment both end, as with "sign out
            // all other devices"; the person signs in again.
            if (!await db.Sessions.AnyAsync(s => s.Id == current && s.UserId == userId, cancellationToken))
            {
                return NotSignedIn();
            }

            // Every other session, expired ones too: none of them may outlive the password it was signed in with. The
            // lockout's reset and the event share this transaction (the services use the request's own context).
            await db.Sessions.Where(s => s.UserId == userId && s.Id != current).ExecuteDeleteAsync(cancellationToken);
            await throttle.ResetAsync(throttleKey, cancellationToken);
            await audit.WriteAsync(http, AuditEventTypes.PasswordChanged, userId, cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        sessionEvents.AllSessionsEndedExcept(userId, current);
        return Results.NoContent();
    }

    private static IResult CurrentPasswordInvalid() =>
        ApiResults.Error(StatusCodes.Status400BadRequest, "current_password_invalid", "currentPassword");

    /// <summary>The answer the cookie handler gives a request without a session, so the client says the same.</summary>
    private static IResult NotSignedIn() => ApiResults.Error(StatusCodes.Status401Unauthorized, "not_authenticated");
}
