using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;

namespace Inkpact.Api.Versions;

/// <summary>
/// The running version, whether it is a developer's build (unstamped, which never asks for updates), and for an
/// administrator the newer release when there is one (run B.8). The client takes the server's word on all three.
/// </summary>
public sealed record VersionDto(string Version, bool Development, AvailableUpdate? Update);

public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        // Signed in, not anonymous: a version tells an attacker which advisories apply. The update is for the
        // administrator, who is the one who can install it.
        app.MapGet("/api/version", async (HttpContext http, AppVersion version, UpdateCheck check, IPermissionService permissions) =>
        {
            var admin = await permissions.IsAdminAsync(http.User.UserId(), http.RequestAborted);
            return Results.Ok(new VersionDto(version.Text, version.Release.IsDevelopment, admin ? check.Update : null));
        }).RequireAuthorization(Policies.SignedIn);
        return app;
    }
}
