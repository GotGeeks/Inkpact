using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Permissions;

/// <summary>Marks an endpoint as needing a role in the workspace or page named by a route parameter. The coverage test reads it.</summary>
public sealed record RoleRequirement(RoleScope Scope, WorkspaceRole MinimumRole, string RouteParameter);

public enum RoleScope
{
    Workspace,
    Page,
    Attachment,
    Import,
}

public static class PermissionEndpointExtensions
{
    /// <summary>Signed in, and at least this role in the workspace whose id is in the route.</summary>
    public static RouteHandlerBuilder RequireWorkspaceRole(this RouteHandlerBuilder builder, WorkspaceRole minimum, string routeParameter = "workspaceId") =>
        builder.RequireRole(new RoleRequirement(RoleScope.Workspace, minimum, routeParameter));

    /// <summary>Signed in, and at least this role on the page whose id is in the route.</summary>
    public static RouteHandlerBuilder RequirePageRole(this RouteHandlerBuilder builder, WorkspaceRole minimum, string routeParameter = "pageId") =>
        builder.RequireRole(new RoleRequirement(RoleScope.Page, minimum, routeParameter));

    /// <summary>Signed in, and at least this role on the page the attachment whose id is in the route belongs to.</summary>
    public static RouteHandlerBuilder RequireAttachmentRole(this RouteHandlerBuilder builder, WorkspaceRole minimum, string routeParameter = "attachmentId") =>
        builder.RequireRole(new RoleRequirement(RoleScope.Attachment, minimum, routeParameter));

    /// <summary>Signed in, the one who started the import whose id is in the route, and at least this role in its workspace.</summary>
    public static RouteHandlerBuilder RequireImportRole(this RouteHandlerBuilder builder, WorkspaceRole minimum, string routeParameter = "importId") =>
        builder.RequireRole(new RoleRequirement(RoleScope.Import, minimum, routeParameter));

    private static RouteHandlerBuilder RequireRole(this RouteHandlerBuilder builder, RoleRequirement requirement) =>
        builder
            .RequireAuthorization(Policies.SignedIn)
            .WithMetadata(requirement)
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                // A malformed or unknown id looks the same as one that belongs to somebody else.
                if (!Guid.TryParse(http.Request.RouteValues[requirement.RouteParameter]?.ToString(), out var id)
                    || !Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    return NotFound();
                }

                var permissions = http.RequestServices.GetRequiredService<IPermissionService>();
                var role = requirement.Scope switch
                {
                    RoleScope.Workspace => await permissions.GetWorkspaceRoleAsync(userId, id, http.RequestAborted),
                    RoleScope.Page => await permissions.GetPageRoleAsync(userId, id, http.RequestAborted),
                    RoleScope.Attachment => await permissions.GetAttachmentRoleAsync(userId, id, http.RequestAborted),
                    RoleScope.Import => await permissions.GetImportRoleAsync(userId, id, http.RequestAborted),
                    _ => WorkspaceRole.None,
                };

                // No access at all: 404, so the existence of other people's data does not leak.
                // Some access, but not enough: 403.
                if (role == WorkspaceRole.None)
                {
                    return NotFound();
                }

                return role < requirement.MinimumRole
                    ? Results.Json(new ApiError("forbidden"), statusCode: StatusCodes.Status403Forbidden)
                    : await next(context);
            });

    private static IResult NotFound() => Results.Json(new ApiError("not_found"), statusCode: StatusCodes.Status404NotFound);
}
