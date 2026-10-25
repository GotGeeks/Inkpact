using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Inkpact.Data;
using Inkpact.Data.Entities;
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

public sealed record AuthState(bool SetupRequired, CurrentUser? User);

public static class AuthEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxEmailLength = 320;

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapGet("/state", GetState).AllowAnonymous();
        group.MapPost("/setup", Setup).AllowAnonymous();
        group.MapPost("/login", Login).AllowAnonymous();
        group.MapPost("/logout", Logout).RequireAuthorization();
        group.MapPost("/logout-everywhere", LogoutEverywhere).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> GetState(HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var user = await LoadUser(http, db, cancellationToken);
        var setupRequired = user is null && !await db.Users.AnyAsync(cancellationToken);
        return Results.Ok(new AuthState(setupRequired, user));
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

        try
        {
            // Serializable: two requests that both saw an empty table cannot both commit.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (await db.Users.AnyAsync(cancellationToken))
            {
                return Error(StatusCodes.Status409Conflict, "setup_not_available");
            }

            db.Users.Add(user);
            db.Workspaces.Add(new Workspace { Name = workspaceName, OwnerId = user.Id, CreatedAt = now });
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
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? "";
        var user = email is null
            ? null
            : await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == email, cancellationToken);

        if (user?.PasswordHash is null)
        {
            // Same work and same answer as a wrong password, so the response does not say which emails exist.
            hasher.VerifyDummy(password);
            return Error(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        if (!hasher.Verify(user.PasswordHash, password))
        {
            return Error(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        if (hasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = hasher.Hash(password);
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignIn(http, user);
        return Results.NoContent();
    }

    private static async Task Logout(HttpContext http)
    {
        // The cookie handler removes this session's row from the store.
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task<IResult> LogoutEverywhere(HttpContext http, PostgresTicketStore store)
    {
        await store.RemoveAllForUserAsync(GetUserId(http.User));
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
/// API responses are never cached. Every state-changing API call must be JSON. A cross-site form or fetch cannot send that without a CORS
/// preflight, which the app never allows, so this is the CSRF defence until the tokens of run 1.06.
/// Attachment uploads (run 1.14) will need their own, authenticated, exception.
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
