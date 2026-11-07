using System.Security.Claims;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Links;

/// <summary>A page that links to another: its id and title, and its parent's title so that pages with one title can be told apart.</summary>
public sealed record BacklinkDto(Guid Id, string Title, string? ParentTitle);

/// <summary>The pages that link to a page, at most <see cref="BacklinkEndpoints.MaxBacklinks"/> of them; <c>More</c> when there are more.</summary>
public sealed record BacklinksDto(IReadOnlyList<BacklinkDto> Items, bool More);

/// <summary>
/// The backlinks of a page (run B.1): the pages whose content links to it, from the link index. Only pages the person may
/// see and that are live (not in the trash, nor below something that is), so a backlink never shows a title that the tree hides.
/// </summary>
public static class BacklinkEndpoints
{
    public const int MaxBacklinks = 200;

    public static IEndpointRouteBuilder MapBacklinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pages/{pageId}/backlinks", GetBacklinks).RequirePageRole(WorkspaceRole.Viewer);
        return app;
    }

    private static async Task<IResult> GetBacklinks(
        Guid pageId, HttpContext http, InkpactDbContext db, IPermissionService permissions, CancellationToken cancellationToken)
    {
        var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : Guid.Empty;

        // The page itself is never its own backlink, whatever the index holds.
        var sources = await db.PageLinks.AsNoTracking()
            .Where(l => l.TargetPageId == pageId && l.SourcePageId != pageId)
            .Join(db.Pages.AsNoTracking(), l => l.SourcePageId, p => p.Id, (_, p) => new { p.Id, p.WorkspaceId, p.Title, p.ParentId })
            .ToListAsync(cancellationToken);

        var visible = new List<(Guid Id, string Title, Guid? ParentId)>();
        foreach (var inWorkspace in sources.GroupBy(s => s.WorkspaceId))
        {
            // Not what the person may not see, whichever workspace the link comes from.
            if (await permissions.GetWorkspaceRoleAsync(userId, inWorkspace.Key, cancellationToken) < WorkspaceRole.Viewer)
            {
                continue;
            }

            // The same function the tree and search read: not the trash, nor anything below it.
            Guid[] ids = [.. inWorkspace.Select(s => s.Id)];
            var live = (await db.Database
                .SqlQuery<Guid>($"""SELECT page_id AS "Value" FROM live_pages({inWorkspace.Key}) WHERE page_id = ANY({ids})""")
                .ToListAsync(cancellationToken)).ToHashSet();
            visible.AddRange(inWorkspace.Where(s => live.Contains(s.Id)).Select(s => (s.Id, s.Title, s.ParentId)));
        }

        var page = visible
            .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id)
            .Take(MaxBacklinks)
            .ToList();
        Guid[] parentIds = [.. page.Where(s => s.ParentId is not null).Select(s => s.ParentId!.Value).Distinct()];
        var parentTitles = await db.Pages.AsNoTracking()
            .Where(p => parentIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);

        return Results.Ok(new BacklinksDto(
            [.. page.Select(s => new BacklinkDto(s.Id, s.Title, s.ParentId is { } parent ? parentTitles.GetValueOrDefault(parent) : null))],
            visible.Count > MaxBacklinks));
    }
}
