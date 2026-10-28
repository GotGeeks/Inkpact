using System.Security.Claims;
using System.Text.Json;
using Inkpact.Api.Attachments;
using Inkpact.Api.Auth;
using Inkpact.Api.Permissions;
using Inkpact.Api.Security;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inkpact.Api.Import;

/// <summary>What the person who started an import sees: where it is, and when it has ended, how it went.</summary>
public sealed record ImportDto(
    Guid Id,
    Guid WorkspaceId,
    string FileName,
    string State,
    string? ErrorCode,
    int TotalItems,
    int ProcessedItems,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    JsonElement Counts,
    JsonElement? Report);

public sealed record ImportAccepted(Guid Id);

public sealed record ImportLimits(long MaxFileSize);

/// <summary>
/// Importing a Joplin export. The upload needs the editor role in the workspace; the job runs in the background, and its
/// progress and report can be read by the person who started it. The export is one large file, so the upload is
/// streamed to disk and has a limit of its own.
/// </summary>
public static class ImportEndpoints
{
    public static IServiceCollection AddImport(this IServiceCollection services, IConfiguration config)
    {
        var options = new ImportOptions();
        config.GetSection(ImportOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<ImportQueue>();
        services.AddSingleton<ImportRunner>();
        services.AddHostedService<ImportWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        // Like the attachment limit: asked first, so the client does not send a file the server will refuse.
        app.MapGet("/api/imports/limits", (ImportOptions options) => Results.Ok(new ImportLimits(options.MaxFileSize)))
            .RequireAuthorization(Policies.SignedIn);
        app.MapPost("/api/workspaces/{workspaceId}/imports/joplin", UploadJoplin).RequireWorkspaceRole(WorkspaceRole.Editor).AcceptsFileUpload();
        app.MapGet("/api/imports/{importId}", GetImport).RequireImportRole(WorkspaceRole.Viewer);
        return app;
    }

    /// <summary>
    /// The raw JEX file is the body; its name is in the query, as it is for an attachment. The import's row is made
    /// first, as a claim: the database takes one waiting or running import per workspace, so of two uploads at the same
    /// time one is refused before it has sent anything. The file is written next, and when that fails the claim goes again.
    /// </summary>
    private static async Task<IResult> UploadJoplin(
        Guid workspaceId,
        string? name,
        HttpContext http,
        InkpactDbContext db,
        ImportOptions options,
        ImportQueue queue,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var max = options.MaxFileSize;
        if (http.Request.ContentLength > max)
        {
            return TooLarge();
        }

        if (http.Request.ContentLength == 0)
        {
            return Error("import_empty", StatusCodes.Status400BadRequest);
        }

        var importId = Guid.CreateVersion7();
        queue.Claim(importId);
        var enqueued = false;
        try
        {
            await ReleaseLeftBehindAsync(db, queue, workspaceId, time, cancellationToken);

            var import = new ImportRun
            {
                Id = importId,
                WorkspaceId = workspaceId,
                UserId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
                SourceType = ImportRunner.SourceType,
                FileName = AttachmentFiles.CleanFileName(name),
                State = ImportStates.Queued,
                CreatedAt = time.GetUtcNow(),
            };
            db.Imports.Add(import);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // One at a time per workspace: a second export while the first is running would only fill the disk.
                return Error("import_in_progress", StatusCodes.Status409Conflict);
            }

            var refused = await ReceiveFileAsync(http, options, importId, cancellationToken);
            if (refused is not null)
            {
                await db.Imports.Where(i => i.Id == importId).ExecuteDeleteAsync(CancellationToken.None);
                return refused;
            }

            import.FileSize = new FileInfo(ImportFiles.PathOf(options, importId)).Length;
            await db.SaveChangesAsync(CancellationToken.None);
            queue.Enqueue(importId);
            enqueued = true;
            return Results.Json(new ImportAccepted(importId), statusCode: StatusCodes.Status202Accepted);
        }
        catch when (!enqueued)
        {
            // The connection dropped, or the disk is full: neither the claim nor the file is left behind.
            DeleteQuietly(ImportFiles.PathOf(options, importId));
            await db.Imports.Where(i => i.Id == importId).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (!enqueued)
            {
                queue.Release(importId);
            }
        }
    }

    /// <summary>
    /// Marks the workspace's imports that say they wait or run but that no job here works on: what a job that ended
    /// without a word (the database went away under it) left. Without this, such a row would block the workspace until
    /// the app restarts.
    /// </summary>
    private static async Task ReleaseLeftBehindAsync(InkpactDbContext db, ImportQueue queue, Guid workspaceId, TimeProvider time, CancellationToken cancellationToken)
    {
        var open = await db.Imports.AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && (i.State == ImportStates.Queued || i.State == ImportStates.Running))
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
        var leftBehind = open.Where(id => !queue.IsActive(id)).ToList();
        if (leftBehind.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        await db.Imports
            .Where(i => leftBehind.Contains(i.Id) && (i.State == ImportStates.Queued || i.State == ImportStates.Running))
            .ExecuteUpdateAsync(
                set => set.SetProperty(i => i.State, ImportStates.Failed).SetProperty(i => i.ErrorCode, "interrupted").SetProperty(i => i.FinishedAt, now),
                cancellationToken);
    }

    /// <summary>Writes the body to the import's file. Null when it is kept; otherwise the answer, and the file is gone.</summary>
    private static async Task<IResult?> ReceiveFileAsync(HttpContext http, ImportOptions options, Guid importId, CancellationToken cancellationToken)
    {
        var max = options.MaxFileSize;
        // Kestrel's own limit is for JSON; here it is the setting, one byte over, so the count below says what is too large.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max + 1;
        }

        var path = ImportFiles.PathOf(options, importId);
        Directory.CreateDirectory(options.Directory);
        try
        {
            long size;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                size = await CopyAsync(http.Request.Body, file, max, cancellationToken);
            }

            if (size == 0)
            {
                File.Delete(path);
                return Error("import_empty", StatusCodes.Status400BadRequest);
            }

            if (!await JexArchive.LooksLikeTarAsync(path, cancellationToken))
            {
                File.Delete(path);
                return Error("import_invalid", StatusCodes.Status400BadRequest);
            }

            return null;
        }
        catch (FileTooLargeException)
        {
            DeleteQuietly(path);
            return TooLarge();
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            DeleteQuietly(path);
            return TooLarge();
        }
    }

    /// <summary>For the person who started the import, while they have a role in its workspace (the endpoint's filter).</summary>
    private static async Task<IResult> GetImport(Guid importId, InkpactDbContext db, CancellationToken cancellationToken)
    {
        var import = await db.Imports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, cancellationToken);
        if (import is null)
        {
            return Error("not_found", StatusCodes.Status404NotFound);
        }

        var ended = import.State is ImportStates.Completed or ImportStates.Failed;
        using var counts = JsonDocument.Parse(import.Counts);
        using var report = ended ? JsonDocument.Parse(import.Report) : null;
        return Results.Json(new ImportDto(
            import.Id,
            import.WorkspaceId,
            import.FileName,
            import.State,
            import.ErrorCode,
            import.TotalItems,
            import.ProcessedItems,
            import.CreatedAt,
            import.StartedAt,
            import.FinishedAt,
            counts.RootElement.Clone(),
            report?.RootElement.Clone()));
    }

    /// <summary>Copies the request to the file, and stops the moment it is over the limit.</summary>
    private static async Task<long> CopyAsync(Stream source, Stream target, long max, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > max)
            {
                throw new FileTooLargeException(max);
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing more to do: the worker's start-up removes old files.
        }
    }

    private static IResult TooLarge() => Error("import_too_large", StatusCodes.Status413PayloadTooLarge);

    private static IResult Error(string code, int status) => Results.Json(new ApiError(code), statusCode: status);
}
