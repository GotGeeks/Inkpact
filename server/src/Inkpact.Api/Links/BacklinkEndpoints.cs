using System.Security.Claims;
using Inkpact.Api.Pages;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Links;

/// <summary>The pages that link to a page, at most <see cref="BacklinkEndpoints.MaxBacklinks"/> of them; <c>More</c> when there are more.</summary>
public sealed record BacklinksDto(IReadOnlyList<ListedPage> Items, bool More);

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

        // Live first: the same function the tree and search read, so not the trash, nor anything below it.
        var live = new List<(Guid Id, string Title, Guid? ParentId)>();
        foreach (var inWorkspace in sources.GroupBy(s => s.WorkspaceId))
        {
            Guid[] ids = [.. inWorkspace.Select(s => s.Id)];
            var alive = (await db.Database
                .SqlQuery<Guid>($"""SELECT page_id AS "Value" FROM live_pages({inWorkspace.Key}) WHERE page_id = ANY({ids})""")
                .ToListAsync(cancellationToken)).ToHashSet();
            live.AddRange(inWorkspace.Where(s => alive.Contains(s.Id)).Select(s => (s.Id, s.Title, s.ParentId)));
        }

        // Then what the person may see, page by page, through the one place that decides it (shares of a page arrive there
        // in run 2.2). In title order, and only until one more than fits, which is all the answer needs.
        var page = new List<(Guid Id, string Title, Guid? ParentId)>();
        var more = false;
        foreach (var source in live.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id))
        {
            if (await permissions.GetPageRoleAsync(userId, source.Id, cancellationToken) < WorkspaceRole.Viewer)
            {
                continue;
            }

            if (page.Count == MaxBacklinks)
            {
                more = true;
                break;
            }

            page.Add(source);
        }

        return Results.Ok(new BacklinksDto(await ListedPages.WithParentTitlesAsync(db, page, cancellationToken), more));
    }
}
