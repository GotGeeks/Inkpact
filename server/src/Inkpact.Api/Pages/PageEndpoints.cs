using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Api.Sync;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Pages;

/// <summary>
/// A new page. <c>Id</c> and <c>Content</c> are for a client that makes a page with content in one step (a copy of edits
/// that would be lost): the id it chose, so that sending the same request again gives the same page rather than a second
/// one, and the page's first Yjs update, in base64, stored with the page.
/// </summary>
public sealed record CreatePageRequest(string? Title, Guid? ParentId, Guid? AfterId, Guid? BeforeId, Guid? Id = null, string? Content = null);

public sealed record RenamePageRequest(string? Title, int? ExpectedVersion);

public sealed record MovePageRequest(Guid? ParentId, Guid? AfterId, Guid? BeforeId, int? ExpectedVersion);

public sealed record VersionedRequest(int? ExpectedVersion);

public sealed record PageDetail(PageDto Page, bool InTrash, Guid WorkspaceId);

/// <summary>
/// The page tree over REST. Tree operations never go through the CRDT: they go here, one at a time. Reading needs
/// the viewer role, changing the editor role, on the workspace the page belongs to.
/// </summary>
public static class PageEndpoints
{
    public static IEndpointRouteBuilder MapPageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workspaces/{workspaceId}/pages", GetTree).RequireWorkspaceRole(WorkspaceRole.Viewer);
        app.MapGet("/api/workspaces/{workspaceId}/trash", GetTrash).RequireWorkspaceRole(WorkspaceRole.Viewer);
        app.MapPost("/api/workspaces/{workspaceId}/pages", Create).RequireWorkspaceRole(WorkspaceRole.Editor);

        app.MapGet("/api/pages/{pageId}", GetPage).RequirePageRole(WorkspaceRole.Viewer);
        app.MapPatch("/api/pages/{pageId}", Rename).RequirePageRole(WorkspaceRole.Editor);
        app.MapPost("/api/pages/{pageId}/move", Move).RequirePageRole(WorkspaceRole.Editor);
        app.MapPost("/api/pages/{pageId}/trash", Trash).RequirePageRole(WorkspaceRole.Editor);
        app.MapPost("/api/pages/{pageId}/restore", Restore).RequirePageRole(WorkspaceRole.Editor);

        return app;
    }

    private static async Task<IResult> GetTree(Guid workspaceId, TreeService tree, CancellationToken cancellationToken) =>
        Results.Ok(await tree.GetTreeAsync(workspaceId, cancellationToken));

    private static async Task<IResult> GetTrash(Guid workspaceId, TreeService tree, CancellationToken cancellationToken) =>
        Results.Ok(await tree.GetTrashAsync(workspaceId, cancellationToken));

    private static async Task<IResult> GetPage(Guid pageId, TreeService tree, CancellationToken cancellationToken) =>
        await tree.GetPageAsync(pageId, cancellationToken) is { } found
            ? Results.Ok(new PageDetail(found.Page, found.InTrash, found.WorkspaceId))
            : NotFound();

    private static async Task<IResult> Create(
        Guid workspaceId, CreatePageRequest request, HttpContext http, TreeService tree, SyncOptions sync, CancellationToken cancellationToken)
    {
        PageSeed? seed = null;
        if (request.Id is { } id)
        {
            if (id == Guid.Empty)
            {
                return Respond(new TreeError(StatusCodes.Status400BadRequest, "id_invalid", "id"));
            }

            byte[]? content = null;
            if (request.Content is { } encoded)
            {
                // Content larger than a sync message could not reach the page the other way either.
                if (encoded.Length > (sync.MaxMessageBytes + 2) / 3 * 4)
                {
                    return Respond(new TreeError(StatusCodes.Status413PayloadTooLarge, "content_too_large", "content"));
                }

                try
                {
                    content = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    return Respond(new TreeError(StatusCodes.Status400BadRequest, "content_invalid", "content"));
                }

                if (content.Length == 0)
                {
                    return Respond(new TreeError(StatusCodes.Status400BadRequest, "content_invalid", "content"));
                }
            }

            var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : (Guid?)null;
            seed = new PageSeed(id, content, userId);
        }
        else if (request.Content is not null)
        {
            // Content only comes with an id: sent twice without one, it would make two pages.
            return Respond(new TreeError(StatusCodes.Status400BadRequest, "id_invalid", "id"));
        }

        var position = new Position(request.ParentId, request.AfterId, request.BeforeId);
        return Respond(await tree.CreateAsync(workspaceId, request.Title, position, seed, cancellationToken), StatusCodes.Status201Created);
    }

    private static async Task<IResult> Rename(Guid pageId, RenamePageRequest request, TreeService tree, CancellationToken cancellationToken) =>
        Respond(await tree.RenameAsync(pageId, request.Title, request.ExpectedVersion, cancellationToken));

    private static async Task<IResult> Move(Guid pageId, MovePageRequest request, TreeService tree, CancellationToken cancellationToken) =>
        Respond(await tree.MoveAsync(pageId, new Position(request.ParentId, request.AfterId, request.BeforeId), request.ExpectedVersion, cancellationToken));

    private static async Task<IResult> Trash(Guid pageId, VersionedRequest request, TreeService tree, CancellationToken cancellationToken) =>
        Respond(await tree.TrashAsync(pageId, request.ExpectedVersion, cancellationToken));

    private static async Task<IResult> Restore(Guid pageId, VersionedRequest request, TreeService tree, CancellationToken cancellationToken) =>
        Respond(await tree.RestoreAsync(pageId, request.ExpectedVersion, cancellationToken));

    private static IResult Respond(TreeResult<PageChange> result, int successStatus = StatusCodes.Status200OK) =>
        result.Error is { } error
            ? Results.Json(new ApiError(error.Code, error.Field), statusCode: error.Status)
            : Results.Json(result.Value, statusCode: successStatus);

    private static IResult NotFound() => Results.Json(new ApiError("not_found"), statusCode: StatusCodes.Status404NotFound);
}
