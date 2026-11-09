using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Pages;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Favorites;

/// <summary>A page the person opened, and when.</summary>
public sealed record RecentPageDto(Guid PageId, DateTimeOffset OpenedAt);

/// <summary>
/// Favorites and recently opened pages (run B.3), kept per person on the server so that the phone and the laptop share
/// them. Private: every query is filtered by the signed-in user, and nothing here touches a page's version, its update
/// time or the tree version, nor writes an audit line.
/// <para>
/// A change locks the person's own user row for its transaction, so two changes by one person (two devices, two tabs)
/// run one after the other: the limit of favorites cannot be passed by two stars at once, and two visits never prune the
/// same rows at the same moment. Different people never wait for each other.
/// </para>
/// <para>
/// Phase 2: the lists filter by the workspace role only. When page shares come (run 2.3), the list queries must go through
/// the permission service, as the backlinks do.
/// </para>
/// </summary>
public static class FavoriteEndpoints
{
    public const int MaxFavorites = 200;
    public const int MaxVisits = 50;

    public static IEndpointRouteBuilder MapFavoriteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/pages/{pageId}/favorite", Star).RequirePageRole(WorkspaceRole.Viewer);
        app.MapDelete("/api/pages/{pageId}/favorite", Unstar).RequirePageRole(WorkspaceRole.Viewer);
        app.MapGet("/api/workspaces/{workspaceId}/favorites", GetFavorites).RequireWorkspaceRole(WorkspaceRole.Viewer);

        app.MapPost("/api/pages/{pageId}/visit", Visit).RequirePageRole(WorkspaceRole.Viewer);
        app.MapGet("/api/workspaces/{workspaceId}/recent", GetRecent).RequireWorkspaceRole(WorkspaceRole.Viewer);
        return app;
    }

    private static Guid UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    private static IResult Error(int status, string code) => Results.Json(new ApiError(code), statusCode: status);

    /// <summary>Takes the person's user row for the rest of the transaction: their changes here run one at a time.</summary>
    private static Task LockUserAsync(InkpactDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Users.FromSql($"select * from users where id = {userId} for update").AsNoTracking().ToListAsync(cancellationToken);

    private static async Task<IResult> Star(
        Guid pageId, HttpContext http, InkpactDbContext db, TreeService tree, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = UserId(http);
        var workspaceId = await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.WorkspaceId).SingleAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAsync(db, userId, cancellationToken);

        // The trash view opens a page in the trash, but it is not starred: it would be in a list that hides it. The walk up
        // from the page alone, not the whole workspace's tree.
        if (!await tree.IsLiveAsync(pageId, cancellationToken))
        {
            return Error(StatusCodes.Status409Conflict, "page_in_trash");
        }

        if (await db.Favorites.AnyAsync(f => f.UserId == userId && f.PageId == pageId, cancellationToken))
        {
            return Results.NoContent();
        }

        var count = await db.Favorites.CountAsync(
            f => f.UserId == userId && db.Pages.Any(p => p.Id == f.PageId && p.WorkspaceId == workspaceId), cancellationToken);
        if (count >= MaxFavorites)
        {
            return Error(StatusCodes.Status409Conflict, "too_many_favorites");
        }

        db.Favorites.Add(new Favorite { UserId = userId, PageId = pageId, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> Unstar(Guid pageId, HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(http);
        await db.Favorites.Where(f => f.UserId == userId && f.PageId == pageId).ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The ids of the person's favorite live pages (as the tree shows them: <c>live_pages</c>), in starring order.</summary>
    private static async Task<IResult> GetFavorites(Guid workspaceId, HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(http);
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT f.page_id AS "Value"
            FROM favorites f JOIN live_pages({workspaceId}) live ON live.page_id = f.page_id
            WHERE f.user_id = {userId}
            ORDER BY f.created_at, f.page_id
            """).ToListAsync(cancellationToken);
        return Results.Ok(ids);
    }

    private static async Task<IResult> Visit(
        Guid pageId, HttpContext http, InkpactDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var userId = UserId(http);
        var workspaceId = await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.WorkspaceId).SingleAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAsync(db, userId, cancellationToken);

        var now = time.GetUtcNow();
        var updated = await db.PageVisits
            .Where(v => v.UserId == userId && v.PageId == pageId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.OpenedAt, now), cancellationToken);
        if (updated == 0)
        {
            db.PageVisits.Add(new PageVisit { UserId = userId, PageId = pageId, OpenedAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }

        // Keep the newest MaxVisits of this workspace; the rest go.
        var inWorkspace = db.PageVisits.Where(v => v.UserId == userId && db.Pages.Any(p => p.Id == v.PageId && p.WorkspaceId == workspaceId));
        var keep = inWorkspace.OrderByDescending(v => v.OpenedAt).ThenBy(v => v.PageId).Take(MaxVisits).Select(v => v.PageId);
        await inWorkspace.Where(v => !keep.Contains(v.PageId)).ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The person's opened live pages, newest first, at most <see cref="MaxVisits"/>.</summary>
    private static async Task<IResult> GetRecent(Guid workspaceId, HttpContext http, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var userId = UserId(http);
        var rows = await db.Database.SqlQuery<VisitRow>($"""
            SELECT v.page_id AS "PageId", v.opened_at AS "OpenedAt"
            FROM page_visits v JOIN live_pages({workspaceId}) live ON live.page_id = v.page_id
            WHERE v.user_id = {userId}
            ORDER BY v.opened_at DESC, v.page_id
            LIMIT {MaxVisits}
            """).ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(r => new RecentPageDto(r.PageId, r.OpenedAt)));
    }

    // What the raw query returns; the names match the aliases in the SQL.
    private sealed class VisitRow
    {
        public Guid PageId { get; set; }

        public DateTimeOffset OpenedAt { get; set; }
    }
}
