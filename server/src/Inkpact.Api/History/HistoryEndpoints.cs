using System.Globalization;
using System.Text.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Crdt;
using Inkpact.Api.Permissions;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

/// <summary>Who made a version, when that is one person: a rename, a restore. Null for an automatic version.</summary>
public sealed record VersionAuthor(Guid Id, string DisplayName);

/// <summary>
/// A version in the list: everything but its content. <paramref name="ProtectedUntil"/> is set while no retention or cap
/// removes it: the page as it was before a restore, kept for a day. Null once that time has passed.
/// </summary>
public sealed record VersionSummary(
    Guid Id, DateTimeOffset CreatedAt, string Kind, string Title, VersionAuthor? CreatedBy, long Bytes, DateTimeOffset? ProtectedUntil = null);

/// <summary>
/// One page of a page's versions, newest first. <paramref name="Next"/> asks for the page after it. <paramref name="State"/>
/// says where the page's history stands (<see cref="HistoryStates"/>), and <paramref name="Failure"/> why when it failed.
/// </summary>
/// <remarks>
/// <paramref name="PageContent"/> is the page's content mark (<see cref="ContentMark"/>) when the list was read: a restore
/// sends it back, and is refused when the page has changed since.
/// </remarks>
public sealed record VersionList(IReadOnlyList<VersionSummary> Versions, string? Next, string State, string? Failure, string PageContent);

/// <summary>A version to look at: its title and its content as the editor's document.</summary>
public sealed record VersionDetail(Guid Id, DateTimeOffset CreatedAt, string Kind, string Title, JsonElement Content);

public static class HistoryStates
{
    /// <summary>History is on and everything stored is in a version.</summary>
    public const string Ready = "ready";

    /// <summary>The workspace's history is off: no new versions are made.</summary>
    public const string Off = "off";

    /// <summary>Something is waiting to be captured.</summary>
    public const string Pending = "pending";

    /// <summary>The last attempt failed for a reason that is the page's own (<see cref="PageHistoryFailures.Unreadable"/>).</summary>
    public const string Failed = "failed";

    /// <summary>The workspace's history is at its cap.</summary>
    public const string Full = "full";
}

/// <summary>
/// Lets one version's content be read at a time. Reading it is a replay in the Yjs sidecar, which takes one request at a
/// time: without this, someone going through a list of large versions would keep the sidecar busy, and everyone who opens
/// a page meanwhile would wait behind them. With it, at most one such read is ever in the sidecar's queue.
/// </summary>
internal sealed class HistoryPreviewGate(HistoryOptions options)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Waits for its turn. Null when the wait was too long; otherwise dispose it when done.</summary>
    internal async Task<IDisposable?> EnterAsync(CancellationToken cancellationToken) =>
        await _one.WaitAsync(options.PreviewWait, cancellationToken) ? new Turn(_one) : null;

    private sealed class Turn(SemaphoreSlim one) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                one.Release();
            }
        }
    }
}

/// <summary>
/// Reading a page's history (run B.5b): the list, and one version's content. A viewer of the page may read both. A
/// version is always looked up within the page of the address, so an id of another page's version is not found.
/// </summary>
public static class HistoryEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pages/{pageId}/versions", List).RequirePageRole(WorkspaceRole.Viewer);
        app.MapGet("/api/pages/{pageId}/versions/{versionId}", Get).RequirePageRole(WorkspaceRole.Viewer);
        app.MapPost("/api/pages/{pageId}/versions/{versionId}/restore", Restore).RequirePageRole(WorkspaceRole.Editor);
        return app;
    }

    private static async Task<IResult> List(
        Guid pageId, string? before, int? limit, InkpactDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var versions = db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId);
        if (before is not null)
        {
            if (!TryReadCursor(before, out var beforeTime, out var beforeId))
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "cursor_invalid", "before");
            }

            versions = versions.Where(v => v.CreatedAt < beforeTime || (v.CreatedAt == beforeTime && v.Id.CompareTo(beforeId) < 0));
        }

        var rows = await versions
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.CreatedAt,
                v.Kind,
                v.Title,
                v.CreatedBy,
                AuthorName = db.Users.Where(u => u.Id == v.CreatedBy).Select(u => u.DisplayName).FirstOrDefault(),
                v.PayloadBytes,
                v.ProtectedUntil,
            })
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        // A protection that has passed is not sent: the retention no longer heeds it (see HistoryCap), and a client's own
        // clock should not have to decide.
        var now = time.GetUtcNow();
        var page = rows.Take(take)
            .Select(v => new VersionSummary(
                v.Id, v.CreatedAt, KindName(v.Kind), v.Title,
                v.CreatedBy is { } author && v.AuthorName is not null ? new VersionAuthor(author, v.AuthorName) : null,
                v.PayloadBytes,
                v.ProtectedUntil > now ? v.ProtectedUntil : null))
            .ToList();
        var next = rows.Count > take ? WriteCursor(page[^1].CreatedAt, page[^1].Id) : null;
        var stored = await ContentMark.ReadAsync(db, pageId, cancellationToken);
        var (state, failure) = await StateAsync(db, pageId, stored.StoredUpdates, cancellationToken);
        return Results.Ok(new VersionList(page, next, state, failure, ContentMark.Of(stored.LastSeq, stored.StoredUpdates)));
    }

    private static async Task<IResult> Get(
        Guid pageId, Guid versionId, InkpactDbContext db, IYjsCodec codec, HistoryPreviewGate gate, CancellationToken cancellationToken)
    {
        var versions = db.PageVersions.AsNoTracking().Where(v => v.Id == versionId && v.PageId == pageId);
        var version = await versions.Select(v => new { v.Id, v.CreatedAt, v.Kind, v.Title }).SingleOrDefaultAsync(cancellationToken);
        if (version is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "version_not_found");
        }

        // The content is not left to the browser's cache to save a second read: answers of the API are never cached
        // (JsonOnlyApi). The panel keeps what it has shown for as long as it is open.
        using var turn = await gate.EnterAsync(cancellationToken);
        if (turn is null)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "history_unavailable");
        }

        var state = await versions.Select(v => v.State).SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "version_not_found");
        }

        YjsRead read;
        try
        {
            read = await codec.ReadAsync([state], cancellationToken);
        }
        catch (YjsUnavailableException)
        {
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, "history_unavailable");
        }
        catch (YjsRejectedException)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "version_unreadable");
        }

        // Something the editor's schema does not have was left out: what would be shown is not the version.
        if (read.Skipped != 0)
        {
            return ApiResults.Error(StatusCodes.Status409Conflict, "version_unreadable");
        }

        using var document = JsonDocument.Parse(read.Json);
        return Results.Ok(new VersionDetail(version.Id, version.CreatedAt, KindName(version.Kind), version.Title, document.RootElement.Clone()));
    }

    private static async Task<IResult> Restore(
        Guid pageId, Guid versionId, RestoreRequest request, HttpContext http, HistoryRestore restore, CancellationToken cancellationToken)
    {
        var result = await restore.RestoreAsync(pageId, versionId, http.UserId(), request, cancellationToken);
        return result.Answer is { } answer ? Results.Ok(answer) : ApiResults.Error(result.Status, result.Code!);
    }

    /// <summary>Where the page's history stands, for the list to say so instead of showing an empty list without a word.</summary>
    internal static async Task<(string State, string? Failure)> StateAsync(
        InkpactDbContext db, Guid pageId, long storedUpdates, CancellationToken cancellationToken)
    {
        var enabled = await db.Pages.AsNoTracking()
            .Where(p => p.Id == pageId)
            .Select(p => db.Workspaces.Where(w => w.Id == p.WorkspaceId).Select(w => w.HistoryEnabled).First())
            .SingleOrDefaultAsync(cancellationToken);
        if (!enabled)
        {
            return (HistoryStates.Off, null);
        }

        var state = await db.PageHistoryStates.AsNoTracking().SingleOrDefaultAsync(s => s.PageId == pageId, cancellationToken);
        if (state?.Failure == PageHistoryFailures.Full)
        {
            return (HistoryStates.Full, null);
        }

        if (state?.Failure is { } failure)
        {
            return (HistoryStates.Failed, failure);
        }

        // Counted as the capture counts (see HistoryCapture.DuePagesAsync): the updates in the snapshot and the live ones,
        // as the caller read them with the page's content mark.
        var captured = state?.CapturedUpdates ?? 0;
        var pending = storedUpdates > captured || await db.PageTitleChanges.AnyAsync(c => c.PageId == pageId, cancellationToken);
        return (pending ? HistoryStates.Pending : HistoryStates.Ready, null);
    }

    private static string KindName(PageVersionKind kind) => kind switch
    {
        PageVersionKind.Automatic => "automatic",
        PageVersionKind.Rename => "rename",
        PageVersionKind.Restore => "restore",
        PageVersionKind.BeforeRestore => "before_restore",
        _ => throw new InvalidOperationException("Unknown version kind."),
    };

    /// <summary>The place after a version in the list: its time and its id, since two versions can share a time.</summary>
    internal static string WriteCursor(DateTimeOffset createdAt, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{createdAt.UtcTicks}_{id:N}");

    internal static bool TryReadCursor(string cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        var parts = cursor.Split('_');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[1], "N", out id))
        {
            return false;
        }

        createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}
