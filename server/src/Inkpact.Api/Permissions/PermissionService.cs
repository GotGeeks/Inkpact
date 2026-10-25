using System.Security.Claims;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Permissions;

/// <summary>
/// The only place that decides what someone may do. Endpoints ask it, directly or through
/// <see cref="PermissionEndpointExtensions"/>, and never look at roles themselves. One instance lives for one
/// request and remembers its answers, so asking twice costs one query.
/// </summary>
public interface IPermissionService
{
    /// <summary>The user's role in the workspace, or <see cref="WorkspaceRole.None"/>.</summary>
    Task<WorkspaceRole> GetWorkspaceRoleAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's role on the page, or <see cref="WorkspaceRole.None"/> when there is no such page. Today this is
    /// the role in the page's workspace; shares on pages and their ancestors (run 2.2) are added here, so no
    /// endpoint has to change.
    /// </summary>
    Task<WorkspaceRole> GetPageRoleAsync(Guid userId, Guid pageId, CancellationToken cancellationToken = default);

    Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class PermissionService(InkpactDbContext db) : IPermissionService
{
    private readonly Dictionary<(Guid User, Guid Workspace), WorkspaceRole> _workspaceRoles = [];
    private readonly Dictionary<Guid, Guid?> _pageWorkspaces = [];
    private readonly Dictionary<Guid, bool> _admins = [];

    public async Task<WorkspaceRole> GetWorkspaceRoleAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (_workspaceRoles.TryGetValue((userId, workspaceId), out var cached))
        {
            return cached;
        }

        var role = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.WorkspaceId == workspaceId)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken) ?? WorkspaceRole.None;
        return _workspaceRoles[(userId, workspaceId)] = role;
    }

    public async Task<WorkspaceRole> GetPageRoleAsync(Guid userId, Guid pageId, CancellationToken cancellationToken = default)
    {
        if (!_pageWorkspaces.TryGetValue(pageId, out var workspaceId))
        {
            workspaceId = await db.Pages.AsNoTracking()
                .Where(p => p.Id == pageId)
                .Select(p => (Guid?)p.WorkspaceId)
                .SingleOrDefaultAsync(cancellationToken);
            _pageWorkspaces[pageId] = workspaceId;
        }

        return workspaceId is { } id ? await GetWorkspaceRoleAsync(userId, id, cancellationToken) : WorkspaceRole.None;
    }

    public async Task<bool> IsAdminAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (!_admins.TryGetValue(userId, out var isAdmin))
        {
            // Read from the database on purpose: taking the flag away works at once, not when a session ends.
            isAdmin = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsAdmin, cancellationToken);
            _admins[userId] = isAdmin;
        }

        return isAdmin;
    }
}

/// <summary>The names of the authorisation policies. Every endpoint uses one of them, or is reviewed as anonymous.</summary>
public static class Policies
{
    public const string SignedIn = "signed-in";
    public const string Admin = "admin";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { SignedIn, Admin };

    public static IServiceCollection AddInkpactPolicies(this IServiceCollection services)
    {
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddSingleton<IAuthorizationHandler, AdminHandler>();
        services.AddAuthorizationBuilder()
            // Anything that names no policy at all is denied to anonymous callers.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(SignedIn, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().AddRequirements(new AdminRequirement()));
        return services;
    }
}

public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminHandler : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.Resource is HttpContext http
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            && await http.RequestServices.GetRequiredService<IPermissionService>().IsAdminAsync(userId, http.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }
}
