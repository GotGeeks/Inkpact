using System.Buffers.Text;
using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Api.Sync;
using Inkpact.Api.Tags;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Inkpact.Api.Pages;

/// <summary>
/// A new page. <c>Id</c> and <c>Content</c> are for a client that makes a page with content in one step (a copy of edits
/// that would be lost): the id it chose, so that sending the same request again gives the same page rather than a second
/// one, and the page's first Yjs update, in base64, stored with the page. <c>ExpectedUserId</c> binds an offline copy
/// to the account it belongs to if the browser's session changes before the request arrives.
/// </summary>
public sealed record CreatePageRequest(string? Title, Guid? ParentId, Guid? AfterId, Guid? BeforeId, Guid? Id = null, string? Content = null, Guid? ExpectedUserId = null);

public sealed record RenamePageRequest(string? Title, int? ExpectedVersion);

public sealed record MovePageRequest(Guid? ParentId, Guid? AfterId, Guid? BeforeId, int? ExpectedVersion);

public sealed record VersionedRequest(int? ExpectedVersion);

public sealed record PageDetail(PageDto Page, bool InTrash, Guid WorkspaceId, IReadOnlyList<TagDto> Tags);

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
        // Content may be as large as a page. The limit is set here and not in the handler: the JSON body is read while the
        // parameters are bound, before the handler runs. A little over the content, for the rest of the JSON.
        var sync = app.ServiceProvider.GetRequiredService<SyncOptions>();
        app.MapPost("/api/workspaces/{workspaceId}/pages", Create)
            .RequireWorkspaceRole(WorkspaceRole.Editor)
            .WithMetadata(new RequestSizeLimitAttribute(MaxCreateBodyBytes(sync)));

        app.MapGet("/api/pages/{pageId}", GetPage).RequirePageRole(WorkspaceRole.Viewer);
        app.MapPatch("/api/pages/{pageId}", Rename).RequirePageRole(WorkspaceRole.Editor);
        app.MapPost("/api/pages/{pageId}/move", Move).RequirePageRole(WorkspaceRole.Editor);
        app.MapPost("/api/pages/{pageId}/trash", Trash).RequirePageRole(WorkspaceRole.Editor);
        app.MapPost("/api/pages/{pageId}/restore", Restore).RequirePageRole(WorkspaceRole.Editor);

        return app;
    }

    /// <summary>What the rest of a create request (title, ids, JSON) may add to its content.</summary>
    internal const long RequestOverhead = 64 * 1024;

    /// <summary>
    /// The route's limit for the body of a create request: a coarse cap that Kestrel enforces before the body is read, so a
    /// body far too large is never bound. The exact limit on the content is <see cref="ReadContent"/>'s.
    /// </summary>
    internal static long MaxCreateBodyBytes(SyncOptions sync) =>
        Base64.GetMaxEncodedToUtf8Length(checked((int)sync.MaxCopyBytes)) + RequestOverhead;

    /// <summary>
    /// The content of a create request, decoded: an error when it is not base64, empty, or larger than <paramref name="max"/>
    /// bytes. The length of the text is checked first, so content far too large is never decoded; it is checked again after
    /// decoding, because base64 rounds up to whole groups of three bytes and up to two bytes more would pass the first check.
    /// </summary>
    internal static TreeError? ReadContent(string encoded, long max, out byte[]? content)
    {
        content = null;
        if (encoded.Length > Base64.GetMaxEncodedToUtf8Length(checked((int)max)))
        {
            return new TreeError(StatusCodes.Status413PayloadTooLarge, "content_too_large", "content");
        }

        try
        {
            content = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return new TreeError(StatusCodes.Status400BadRequest, "content_invalid", "content");
        }

        if (content.Length == 0)
        {
            content = null;
            return new TreeError(StatusCodes.Status400BadRequest, "content_invalid", "content");
        }

        if (content.Length > max)
        {
            content = null;
            return new TreeError(StatusCodes.Status413PayloadTooLarge, "content_too_large", "content");
        }

        return null;
    }

    private static async Task<IResult> GetTree(Guid workspaceId, TreeService tree, CancellationToken cancellationToken) =>
        Results.Ok(await tree.GetTreeAsync(workspaceId, cancellationToken));

    private static async Task<IResult> GetTrash(Guid workspaceId, TreeService tree, CancellationToken cancellationToken) =>
        Results.Ok(await tree.GetTrashAsync(workspaceId, cancellationToken));

    private static async Task<IResult> GetPage(Guid pageId, TreeService tree, TagService tags, CancellationToken cancellationToken)
    {
        if (await tree.GetPageAsync(pageId, cancellationToken) is not { } found)
        {
            return ApiResults.NotFound();
        }

        var pageTags = await tags.GetPageTagsAsync(pageId, cancellationToken);
        return Results.Ok(new PageDetail(found.Page, found.InTrash, found.WorkspaceId, pageTags));
    }

    private static async Task<IResult> Create(
        Guid workspaceId, CreatePageRequest request, HttpContext http, TreeService tree, SyncOptions sync, CancellationToken cancellationToken)
    {
        if (request.ExpectedUserId is { } expected
            && (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actual) || actual != expected))
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "session_changed");
        }

        PageSeed? seed = null;
        if (request.Id is { } id)
        {
            // Version 8 is what the import gives the pages it makes (PageIds), worked out in advance so links can point to
            // them: a client that took one first would keep that note from being imported.
            if (id == Guid.Empty || id.Version == 8)
            {
                return Respond(new TreeError(StatusCodes.Status400BadRequest, "id_invalid", "id"));
            }

            byte[]? content = null;
            if (request.Content is { } encoded)
            {
                // Content larger than a page that can be opened could not be used anyway (SyncOptions.MaxCopyBytes).
                if (ReadContent(encoded, sync.MaxCopyBytes, out content) is { } error)
                {
                    return Respond(error);
                }
            }

            Guid? userId = http.UserId();
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
            ? ApiResults.Error(error.Status, error.Code, error.Field)
            : Results.Json(result.Value, statusCode: successStatus);

}
