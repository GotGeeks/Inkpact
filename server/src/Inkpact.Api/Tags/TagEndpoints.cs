using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Tags;

/// <summary>Add tags by name, remove tags by id; either may be left out.</summary>
public sealed record ChangeTagsRequest(IReadOnlyList<string>? Add, IReadOnlyList<Guid>? Remove);

public sealed record RenameTagRequest(string? Name);

/// <summary>
/// Tags over REST (run B.2). A page's tags change through one set operation; the workspace's tags and a tag's pages are
/// read for the "Tags" view. Reading needs the viewer role, changing the editor role, as the tree does.
/// </summary>
public static class TagEndpoints
{
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/pages/{pageId}/tags", ChangePageTags).RequirePageRole(WorkspaceRole.Editor);

        app.MapGet("/api/workspaces/{workspaceId}/tags", GetWorkspaceTags).RequireWorkspaceRole(WorkspaceRole.Viewer);
        app.MapGet("/api/workspaces/{workspaceId}/tags/{tagId}/pages", GetTagPages).RequireWorkspaceRole(WorkspaceRole.Viewer);
        app.MapPatch("/api/workspaces/{workspaceId}/tags/{tagId}", RenameTag).RequireWorkspaceRole(WorkspaceRole.Editor);

        return app;
    }

    private static async Task<IResult> ChangePageTags(
        Guid pageId, ChangeTagsRequest request, TagService tags, CancellationToken cancellationToken) =>
        Respond(await tags.ChangeTagsAsync(pageId, request.Add, request.Remove, cancellationToken));

    private static async Task<IResult> GetWorkspaceTags(Guid workspaceId, TagService tags, CancellationToken cancellationToken) =>
        Results.Ok(await tags.GetWorkspaceTagsAsync(workspaceId, cancellationToken));

    private static async Task<IResult> GetTagPages(Guid workspaceId, Guid tagId, TagService tags, CancellationToken cancellationToken) =>
        Respond(await tags.GetTagPagesAsync(workspaceId, tagId, cancellationToken));

    private static async Task<IResult> RenameTag(
        Guid workspaceId, Guid tagId, RenameTagRequest request, TagService tags, CancellationToken cancellationToken) =>
        Respond(await tags.RenameTagAsync(workspaceId, tagId, request.Name, cancellationToken));

    private static IResult Respond<T>(TagResult<T> result, int successStatus = StatusCodes.Status200OK) =>
        result.Error is { } error
            ? Results.Json(new ApiError(error.Code, error.Field), statusCode: error.Status)
            : Results.Json(result.Value, statusCode: successStatus);
}
