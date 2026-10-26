using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Microsoft.AspNetCore.Authentication;

namespace Inkpact.Api.Sync;

public static class SyncEndpoints
{
    public const string Path = "/api/sync";

    public static IServiceCollection AddSync(this IServiceCollection services, IConfiguration config)
    {
        var options = new SyncOptions();
        config.GetSection(SyncOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<SessionEvents>();
        services.AddSingleton<PageUpdateStore>();
        services.AddSingleton<SyncHub>();
        services.Configure<WebSocketOptions>(webSockets =>
        {
            webSockets.KeepAliveInterval = options.KeepAliveInterval;
            webSockets.KeepAliveTimeout = options.KeepAliveTimeout;
        });
        return services;
    }

    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        // Signed in only. Which pages a socket may receive is decided per page when it subscribes.
        app.MapGet(Path, Connect).RequireAuthorization(Policies.SignedIn);
        return app;
    }

    private static async Task<IResult> Connect(HttpContext http, SyncHub hub)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            return Results.Json(new ApiError("websocket_required"), statusCode: StatusCodes.Status400BadRequest);
        }

        // A WebSocket is not covered by the CSRF token or SameSite rules for fetch: any site the person visits could
        // open one with their cookie. The browser's Origin header says who is asking, so only the app's own pages may.
        if (!IsSameOrigin(http.Request))
        {
            return Results.Json(new ApiError("origin_invalid"), statusCode: StatusCodes.Status403Forbidden);
        }

        var userId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        // Already authenticated for this request, so this reads the cached result.
        var authentication = await http.AuthenticateAsync();
        string? sessionKey = null;
        if (authentication.Properties?.Items.TryGetValue(PostgresTicketStore.SessionKeyItem, out sessionKey) != true
            || string.IsNullOrEmpty(sessionKey))
        {
            return Results.Json(new ApiError("unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!hub.TryReserveConnection(userId))
        {
            return Results.Json(new ApiError("too_many_connections"), statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(socket, userId, sessionKey, http.RequestAborted);
        }
        finally
        {
            hub.ReleaseConnection(userId);
        }

        return Results.Empty;
    }

    /// <summary>
    /// Whether the Origin header names this app as the browser sees it: the scheme after the forwarded headers, and the
    /// Host header (a proxy such as Traefik passes it on unchanged). A request without an Origin is refused: every
    /// browser sends one with a WebSocket.
    /// </summary>
    internal static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && string.Equals(origin, $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);
    }
}
