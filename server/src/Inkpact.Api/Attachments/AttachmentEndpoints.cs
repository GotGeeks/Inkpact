using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Api.Security;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Inkpact.Api.Attachments;

public sealed record AttachmentLimits(long MaxFileSize);

/// <summary>
/// Files on pages. Uploading needs the editor role on the page, downloading the viewer role on the page the file was
/// added to. Content refers to a file by its id only, so these two addresses are the only way to it.
/// </summary>
public static class AttachmentEndpoints
{
    public static IServiceCollection AddAttachments(this IServiceCollection services, IConfiguration config)
    {
        var options = new AttachmentOptions();
        config.GetSection(AttachmentOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<IAttachmentStore>(_ => new LocalAttachmentStore(options));
        services.AddScoped<AttachmentService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        // The client checks the size before it sends a file: refused unread, a large body would often reach the
        // browser as a reset connection rather than as the 413.
        app.MapGet("/api/attachments/limits", (AttachmentOptions options) => Results.Ok(new AttachmentLimits(options.MaxFileSize)))
            .RequireAuthorization(Policies.SignedIn);
        app.MapPost("/api/pages/{pageId}/attachments", Upload).RequirePageRole(WorkspaceRole.Editor).AcceptsFileUpload();
        app.MapGet("/api/attachments/{attachmentId}", Download).RequireAttachmentRole(WorkspaceRole.Viewer).ServesUserFile();
        return app;
    }

    /// <summary>The raw file is the body; its name is in the query, as headers cannot carry every name a file can have.</summary>
    private static async Task<IResult> Upload(
        Guid pageId,
        string? name,
        HttpContext http,
        AttachmentService attachments,
        AttachmentOptions options,
        CancellationToken cancellationToken)
    {
        var max = options.MaxFileSize;
        if (http.Request.ContentLength > max)
        {
            return TooLarge();
        }

        if (http.Request.ContentLength == 0)
        {
            return Empty();
        }

        // Kestrel's own limit (30 MB) is for JSON; here it is the setting, one byte over, so the store's count
        // is what refuses a file without a length, with a clear code.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max + 1;
        }

        AttachmentDto? added;
        try
        {
            var userId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
            added = await attachments.AddAsync(pageId, userId, name, http.Request.Body, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            return TooLarge();
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }

        return added is null ? Empty() : Results.Json(added, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> Download(Guid attachmentId, HttpContext http, AttachmentService attachments, CancellationToken cancellationToken)
    {
        if (await attachments.OpenAsync(attachmentId, cancellationToken) is not { } opened)
        {
            return Results.Json(new ApiError("not_found"), statusCode: StatusCodes.Status404NotFound);
        }

        var (attachment, content) = opened;
        // Images the server recognised are shown; everything else, SVG and HTML included, is only ever downloaded.
        var disposition = new ContentDispositionHeaderValue(AttachmentFiles.IsInline(attachment.ContentType) ? "inline" : "attachment");
        disposition.SetHttpFileName(attachment.FileName);
        var headers = http.Response.Headers;
        headers.ContentDisposition = disposition.ToString();

        // Asked again on every use (a 304 when nothing changed), so access that is taken away is never served
        // from a cache. The content under an id never changes, so its hash is the tag.
        headers.CacheControl = "private, no-cache";

        return Results.File(
            content,
            attachment.ContentType,
            entityTag: new EntityTagHeaderValue($"\"{attachment.Sha256}\""),
            enableRangeProcessing: true);
    }

    private static IResult TooLarge() =>
        Results.Json(new ApiError("attachment_too_large"), statusCode: StatusCodes.Status413PayloadTooLarge);

    private static IResult Empty() =>
        Results.Json(new ApiError("attachment_empty"), statusCode: StatusCodes.Status400BadRequest);
}
