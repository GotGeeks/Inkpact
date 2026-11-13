using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Authentication;
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

/// <summary>
/// A person's own account (run B.9): their display name, and the browsers they are signed in on. Only ever the signed-in
/// person's: no endpoint here takes a user id.
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
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        user.DisplayName = displayName;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new CurrentUser(user.Id, user.Email, user.DisplayName, user.IsAdmin));
    }

    private static async Task<IResult> ListSessions(HttpContext http, PostgresTicketStore store, CancellationToken cancellationToken)
    {
        var current = await CurrentSessionKeyAsync(http);
        if (current is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var (sessions, total) = await store.ListForUserAsync(http.UserId(), current, MaxListed, cancellationToken);
        var summaries = sessions
            .Select(s => new SessionSummary(PostgresTicketStore.HandleOf(s.Id), s.CreatedAt, s.LastSeenAt, s.UserAgent, s.Id == current))
            .ToList();
        return Results.Ok(new SessionList(summaries, total - summaries.Count));
    }

    /// <summary>
    /// Signs one of the person's other devices out; its sync sockets close at once. The current device is refused: the
    /// client signs it out through the menu, which also clears what it kept for offline use.
    /// </summary>
    private static async Task<IResult> EndSession(
        string id, HttpContext http, PostgresTicketStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var current = await CurrentSessionKeyAsync(http);
        if (current is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var userId = http.UserId();
        var key = await store.FindForUserAsync(userId, id, cancellationToken);
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
        var current = await CurrentSessionKeyAsync(http);
        if (current is null)
        {
            return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var userId = http.UserId();
        await audit.WriteAsync(http, AuditEventTypes.SessionsRevoked, userId, cancellationToken: cancellationToken);
        var ended = await store.RemoveAllForUserExceptAsync(userId, current, cancellationToken);
        return Results.Ok(new SessionsEnded(ended));
    }

    /// <summary>The key of the session this request came with (the cookie handler has already read it).</summary>
    internal static async Task<string?> CurrentSessionKeyAsync(HttpContext http)
    {
        var authentication = await http.AuthenticateAsync();
        return authentication.Properties?.Items.TryGetValue(PostgresTicketStore.SessionKeyItem, out var key) == true
            && !string.IsNullOrEmpty(key)
            ? key
            : null;
    }
}
