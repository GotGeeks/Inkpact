using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;
using Microsoft.Net.Http.Headers;

namespace Inkpact.Api.Export;

/// <summary>
/// The export of a workspace as one zip (run 1.18b). Whoever may read the workspace may take all of it, which is why each one
/// that starts is a line in the security log, with the workspace.
/// </summary>
public static class ExportEndpoints
{
    /// <summary>The export cannot be made now (the sidecar does not answer); the client says so in the person's language.</summary>
    public const string Unavailable = "export_unavailable";

    public static IServiceCollection AddExport(this IServiceCollection services) => services.AddScoped<ExportService>();

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/workspaces/{workspaceId}/export/check", Check).RequireWorkspaceRole(WorkspaceRole.Viewer);
        app.MapGet("/api/workspaces/{workspaceId}/export", Export).RequireWorkspaceRole(WorkspaceRole.Viewer);
        return app;
    }

    /// <summary>
    /// Asked by the client right before the download, which is a plain link the app cannot read the answer of: a session that
    /// ended or a sidecar that is down become a message here, as a code the client translates.
    /// </summary>
    private static async Task<IResult> Check(ExportService export, CancellationToken cancellationToken) =>
        await export.CanExportAsync(cancellationToken)
            ? Results.NoContent()
            : Results.Json(new ApiError(Unavailable), statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Export(
        Guid workspaceId,
        string? lang,
        HttpContext http,
        ExportService export,
        IAuditLog audit,
        ILogger<ExportService> logger,
        CancellationToken cancellationToken)
    {
        Guid? userId = http.UserId();

        async Task<Stream> StartAsync(string fileName, CancellationToken token)
        {
            // Written when the first byte is about to leave, not before: an export that ends as a 503 sent nothing.
            await audit.WriteAsync(http, AuditEventTypes.WorkspaceExported, userId, workspaceId: workspaceId, cancellationToken: token);
            var response = http.Response;
            response.ContentType = "application/zip";
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(fileName);
            response.Headers.ContentDisposition = disposition.ToString();
            // Everything of the library: never kept by a cache, and not by the browser either.
            response.Headers.CacheControl = "no-store";
            return response.Body;
        }

        try
        {
            var outcome = await export.WriteAsync(workspaceId, lang, StartAsync, cancellationToken);
            return outcome switch
            {
                ExportOutcome.Written => Results.Empty,
                ExportOutcome.SidecarUnavailable => Results.Json(new ApiError(Unavailable), statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(new ApiError("not_found"), statusCode: StatusCodes.Status404NotFound),
            };
        }
        catch (Exception e) when (http.Response.HasStarted)
        {
            // Part of the zip is on its way, so the answer cannot change. The connection is cut instead of ended, so that the
            // download fails in the browser: a zip that looks whole and is not would be worse than none.
            if (e is not OperationCanceledException)
            {
                logger.LogError(e, "The export of workspace {WorkspaceId} failed after it had started; the connection was cut.", workspaceId);
            }

            http.Abort();
            return Results.Empty;
        }
    }
}
