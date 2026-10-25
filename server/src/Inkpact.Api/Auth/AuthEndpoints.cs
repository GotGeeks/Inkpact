using System.Data;
using System.Globalization;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inkpact.Api.Auth;

/// <summary>An error the client translates: a stable code, and the field it belongs to when there is one.</summary>
public sealed record ApiError(string Code, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

public sealed record SetupRequest(string? SetupCode, string? Email, string? DisplayName, string? Password, string? WorkspaceName);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record CurrentUser(Guid Id, string Email, string DisplayName, bool IsAdmin);

/// <summary>Who is signed in, whether the installation still needs its first account, and the token for the next state-changing call.</summary>
public sealed record AuthState(bool SetupRequired, CurrentUser? User, string? CsrfToken);

public static class AuthEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/state", GetState).AllowAnonymous();
        group.MapPost("/setup", Setup).AllowAnonymous().RequireRateLimiting(ThrottleOptions.RateLimitPolicy);
        group.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(ThrottleOptions.RateLimitPolicy);
        group.MapPost("/logout", Logout).RequireAuthorization(Policies.SignedIn);
        group.MapPost("/logout-everywhere", LogoutEverywhere).RequireAuthorization(Policies.SignedIn);

        return app;
    }

    private static async Task<IResult> GetState(HttpContext http, InkpactDbContext db, IAntiforgery antiforgery, CancellationToken cancellationToken)
    {
        var user = await LoadUser(http, db, cancellationToken);
        var setupRequired = user is null && !await db.Users.AnyAsync(cancellationToken);
        // The token is made for whoever is signed in right now, so it is asked for again after every login and logout.
        var tokens = antiforgery.GetAndStoreTokens(http);
        return Results.Ok(new AuthState(setupRequired, user, tokens.RequestToken));
    }

    private static async Task<IResult> Setup(
        SetupRequest request,
        HttpContext http,
        InkpactDbContext db,
        IPasswordHasher hasher,
        SetupCode setupCode,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return Error(StatusCodes.Status409Conflict, "setup_not_available");
        }

        if (!setupCode.Matches(request.SetupCode))
        {
            return Error(StatusCodes.Status400BadRequest, "setup_code_invalid", "setupCode");
        }

        var email = NormalizeEmail(request.Email);
        var displayName = request.DisplayName?.Trim() ?? "";
        var workspaceName = request.WorkspaceName?.Trim() ?? "";
        var password = request.Password ?? "";

        if (email is null)
        {
            return Error(StatusCodes.Status400BadRequest, "email_invalid", "email");
        }

        if (displayName.Length is 0 or > MaxNameLength)
        {
            return Error(StatusCodes.Status400BadRequest, "display_name_invalid", "displayName");
        }

        if (workspaceName.Length is 0 or > MaxNameLength)
        {
            return Error(StatusCodes.Status400BadRequest, "workspace_name_invalid", "workspaceName");
        }

        if (PasswordPolicy.Validate(password, email) is { } passwordError)
        {
            return Error(StatusCodes.Status400BadRequest, passwordError, "password");
        }

        // The slow part happens before the transaction opens.
        var hash = hasher.Hash(password);
        var now = time.GetUtcNow();
        var user = new User { Email = email, DisplayName = displayName, PasswordHash = hash, IsAdmin = true, CreatedAt = now };
        var workspace = new Workspace { Name = workspaceName, OwnerId = user.Id, CreatedAt = now };

        try
        {
            // Serializable: two requests that both saw an empty table cannot both commit.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (await db.Users.AnyAsync(cancellationToken))
            {
                return Error(StatusCodes.Status409Conflict, "setup_not_available");
            }

            db.Users.Add(user);
            db.Workspaces.Add(workspace);
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, CreatedAt = now });
            // In the same transaction: an account without its audit row, or the other way round, cannot exist.
            db.AuditEvents.Add(AuditLog.Create(http, time, AuditEventTypes.SetupCompleted, user.Id, email));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsSerializationOrUniqueViolation(exception))
        {
            return Error(StatusCodes.Status409Conflict, "setup_not_available");
        }

        await SignIn(http, user);
        return Results.NoContent();
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext http,
        InkpactDbContext db,
        IPasswordHasher hasher,
        LoginThrottling throttle,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? "";
        // Keyed by what was typed, so garbage cannot be used to avoid the count. An address that has no account counts too.
        var key = LoginThrottling.KeyFor(request.Email);

        if (await throttle.GetLockRemainingAsync(key, cancellationToken) is { } lockedFor)
        {
            // Nothing is checked while locked, not even the right password. The hash is still computed, so a
            // locked attempt takes as long as any other and says nothing about the account.
            hasher.VerifyDummy(password);
            await audit.WriteAsync(http, AuditEventTypes.LoginLocked, email: Typed(request.Email), cancellationToken: cancellationToken);
            http.Response.Headers.RetryAfter = RetryAfterSeconds(lockedFor);
            return Error(StatusCodes.Status429TooManyRequests, "login_locked");
        }

        var user = email is null
            ? null
            : await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == email, cancellationToken);

        if (user?.PasswordHash is null || !hasher.Verify(user.PasswordHash, password))
        {
            if (user?.PasswordHash is null)
            {
                // Same work as a wrong password, so the response time does not say which emails exist.
                hasher.VerifyDummy(password);
            }

            await throttle.RecordFailureAsync(key, cancellationToken);
            await audit.WriteAsync(http, AuditEventTypes.LoginFailed, user?.Id, Typed(request.Email), cancellationToken);
            return Error(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        if (hasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = hasher.Hash(password);
            await db.SaveChangesAsync(cancellationToken);
        }

        await throttle.ResetAsync(key, cancellationToken);
        await audit.WriteAsync(http, AuditEventTypes.LoginSucceeded, user.Id, cancellationToken: cancellationToken);
        await SignIn(http, user);
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(HttpContext http, IAuditLog audit, CancellationToken cancellationToken)
    {
        await audit.WriteAsync(http, AuditEventTypes.Logout, GetUserId(http.User), cancellationToken: cancellationToken);
        // The cookie handler removes this session's row from the store.
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutEverywhere(HttpContext http, PostgresTicketStore store, IAuditLog audit, CancellationToken cancellationToken)
    {
        var userId = GetUserId(http.User);
        await audit.WriteAsync(http, AuditEventTypes.LogoutEverywhere, userId, cancellationToken: cancellationToken);
        await store.RemoveAllForUserAsync(userId);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static Task SignIn(HttpContext http, User user)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties { IsPersistent = true };
        var userAgent = http.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > 0)
        {
            properties.Items[PostgresTicketStore.UserAgentItem] = userAgent.Length > 256 ? userAgent[..256] : userAgent;
        }

        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
    }

    /// <summary>The user is read from the database on every call, so a removed or changed account counts at once.</summary>
    private static async Task<CurrentUser?> LoadUser(HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var id = GetUserId(http.User);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new CurrentUser(u.Id, u.Email, u.DisplayName, u.IsAdmin))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static Guid GetUserId(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>What was typed, shortened to what the audit table holds.</summary>
    private static string? Typed(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxEmailLength ? trimmed[..MaxEmailLength] : trimmed;
    }

    private static string RetryAfterSeconds(TimeSpan wait) =>
        Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Trimmed, lower case, and a plausible address. Returns null otherwise.</summary>
    internal static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxEmailLength)
        {
            return null;
        }

        // MailAddress accepts "Name <a@b>" and comments; only a bare address counts.
        return MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed && address.Host.Contains('.')
            ? trimmed.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Another setup won the race. Postgres reports that as a serialization failure, a deadlock or the unique
    /// index on the email, depending on where the two requests meet, and EF may wrap it.
    /// </summary>
    private static bool IsSerializationOrUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private static IResult Error(int status, string code, string? field = null) =>
        Results.Json(new ApiError(code, field), statusCode: status);
}

/// <summary>
/// API responses are never cached. Every state-changing API call must be JSON: a cross-site form or fetch cannot
/// send that without a CORS preflight, which the app never allows. Attachment uploads (run 1.14) will need their
/// own, authenticated, exception.
/// </summary>
public static class JsonOnlyApi
{
    public static IApplicationBuilder UseJsonOnlyApi(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var request = context.Request;
            if (request.Path.StartsWithSegments("/api"))
            {
                // Answers about who is signed in must never come from a cache.
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
            }

            var changesState = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
            if (changesState
                && request.Path.StartsWithSegments("/api")
                && !(request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return context.Response.WriteAsJsonAsync(new ApiError("unsupported_media_type"));
            }

            return next(context);
        });
}
