using System.Security.Claims;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Permissions;

public sealed record WorkspaceSummary(Guid Id, string Name, string Role);

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces");

        group.MapGet("/", ListWorkspaces).RequireAuthorization(Policies.SignedIn);
        group.MapGet("/{workspaceId}", GetWorkspace).RequireWorkspaceRole(WorkspaceRole.Viewer);

        return app;
    }

    /// <summary>The workspaces the signed-in user belongs to, with the role they have in each.</summary>
    private static async Task<IResult> ListWorkspaces(HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var memberships = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Workspace!.Name)
            .Select(m => new { m.Workspace!.Id, m.Workspace.Name, m.Role })
            .ToListAsync(cancellationToken);
        return Results.Ok(memberships.Select(m => new WorkspaceSummary(m.Id, m.Name, RoleName(m.Role))));
    }

    private static async Task<IResult> GetWorkspace(Guid workspaceId, HttpContext http, InkpactDbContext db, IPermissionService permissions, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var name = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.Name).SingleAsync(cancellationToken);
        var role = await permissions.GetWorkspaceRoleAsync(userId, workspaceId, cancellationToken);
        return Results.Ok(new WorkspaceSummary(workspaceId, name, RoleName(role)));
    }

    private static string RoleName(WorkspaceRole role) => role.ToString().ToLowerInvariant();
}
