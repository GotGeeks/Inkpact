using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Pages;

public sealed record CreatePageRequest(string? Title, Guid? ParentId, Guid? AfterId, Guid? BeforeId);

public sealed record RenamePageRequest(string? Title, int? ExpectedVersion);

public sealed record MovePageRequest(Guid? ParentId, Guid? AfterId, Guid? BeforeId, int? ExpectedVersion);

public sealed record VersionedRequest(int? ExpectedVersion);

public sealed record PageDetail(PageDto Page, bool InTrash);

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
            ? Results.Ok(new PageDetail(found.Page, found.InTrash))
            : NotFound();

    private static async Task<IResult> Create(Guid workspaceId, CreatePageRequest request, TreeService tree, CancellationToken cancellationToken) =>
        Respond(await tree.CreateAsync(workspaceId, request.Title, new Position(request.ParentId, request.AfterId, request.BeforeId), cancellationToken), StatusCodes.Status201Created);

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
