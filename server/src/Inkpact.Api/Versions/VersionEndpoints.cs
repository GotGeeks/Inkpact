using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;

namespace Inkpact.Api.Versions;

/// <summary>The running version, and for an administrator the newer release when there is one (run B.8).</summary>
public sealed record VersionDto(string Version, AvailableUpdate? Update);

public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        // Signed in, not anonymous: a version tells an attacker which advisories apply. The update is for the
        // administrator, who is the one who can install it.
        app.MapGet("/api/version", async (HttpContext http, AppVersion version, UpdateCheck check, IPermissionService permissions) =>
        {
            var admin = await permissions.IsAdminAsync(http.User.UserId(), http.RequestAborted);
            return Results.Ok(new VersionDto(version.Text, admin ? check.Update : null));
        }).RequireAuthorization(Policies.SignedIn);
        return app;
    }
}
