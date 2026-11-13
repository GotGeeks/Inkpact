using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Links;
using Inkpact.Api.Pages;
using Inkpact.Api.Permissions;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

/// <summary>
/// What the client asks: restore the version under this operation id, against the page as it saw it.
/// <paramref name="ExpectedContent"/> is the page's content mark from the version list (<see cref="ContentMark"/>), and
/// <paramref name="ExpectedVersion"/> its metadata version (the title and place) as the page view has it.
/// </summary>
public sealed record RestoreRequest(
    [property: JsonRequired] Guid OperationId,
    [property: JsonRequired] string ExpectedContent,
    [property: JsonRequired] int ExpectedVersion);

/// <summary>The answer: the two versions the restore made, and the page's title and metadata version after it.</summary>
public sealed record RestoreAnswer(Guid BeforeVersionId, Guid RestoreVersionId, string Title, int Version);

/// <summary>What a restore ended with. <paramref name="Written"/> is true for the request that made it, not for one repeated.</summary>
internal sealed record RestoreResult(RestoreAnswer? Answer, int Status, string? Code = null, bool Written = false)
{
    internal static RestoreResult Done(RestoreAnswer answer) => new(answer, StatusCodes.Status200OK);

    internal static RestoreResult Refused(int status, string code) => new(null, status, code);
}

/// <summary>The points of a restore where a test can stop it, to see that nothing was written.</summary>
internal enum RestoreStage
{
    /// <summary>Everything read and built, before the locks.</summary>
    Prepared,

    /// <summary>Under every lock, before anything is written.</summary>
    Locked,

    /// <summary>The versions of what was there (the safety version, and titles that waited) are written.</summary>
    BeforeWritten,

    /// <summary>The replacement update is stored.</summary>
    UpdateStored,

    /// <summary>The page, its links, the restore version and the operation are written; the commit is next.</summary>
    PageWritten,
}

internal interface IHistoryRestoreHooks
{
    Task AtAsync(RestoreStage stage, CancellationToken cancellationToken);
}

internal sealed class NoHistoryRestoreHooks : IHistoryRestoreHooks
{
    public Task AtAsync(RestoreStage stage, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A page's stored content, as the version list gives it to the client and a restore checks it: the last sequence number
/// and the number of updates stored (those in the snapshot and the live ones). Both, as the capture has both: the number
/// sees an update that committed late with a lower sequence number, which the sequence number alone would not.
/// </summary>
public static class ContentMark
{
    public static string Of(long lastSeq, long storedUpdates) =>
        string.Create(CultureInfo.InvariantCulture, $"{lastSeq}.{storedUpdates}");

    public static bool TryParse(string? mark, out long lastSeq, out long storedUpdates)
    {
        lastSeq = 0;
        storedUpdates = 0;
        var parts = mark?.Split('.');
        return parts is { Length: 2 }
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out lastSeq)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out storedUpdates);
    }

    /// <summary>A page's content as stored: its mark, and the bytes a client that opens it would be sent as the log.</summary>
    internal sealed record Stored(long LastSeq, long StoredUpdates, long Bytes);

    /// <summary>
    /// The page's mark as stored now, read in one statement: the snapshot row and the live updates as one observation.
    /// Two queries could fall either side of a compaction, which moves updates from the one into the other, and give a
    /// count that is too low (a page that did not change would then seem to have).
    /// </summary>
    internal static async Task<Stored> ReadAsync(InkpactDbContext db, Guid pageId, CancellationToken cancellationToken)
    {
        var row = await db.Pages.AsNoTracking()
            .Where(p => p.Id == pageId)
            .Select(p => new
            {
                Watermark = db.PageSnapshots.Where(s => s.PageId == pageId).Select(s => (long?)s.Watermark).FirstOrDefault() ?? 0,
                InSnapshot = db.PageSnapshots.Where(s => s.PageId == pageId).Select(s => (long?)s.UpdateCount).FirstOrDefault() ?? 0,
                SnapshotBytes = db.PageSnapshots.Where(s => s.PageId == pageId).Select(s => (long?)s.State.Length).FirstOrDefault() ?? 0,
                LastLive = db.PageUpdates.Where(u => u.PageId == pageId).Max(u => (long?)u.Seq) ?? 0,
                Live = db.PageUpdates.LongCount(u => u.PageId == pageId),
                LiveBytes = db.PageUpdates.Where(u => u.PageId == pageId).Sum(u => (long?)u.Data.Length) ?? 0,
            })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null
            ? new Stored(0, 0, 0)
            : new Stored(Math.Max(row.Watermark, row.LastLive), row.InSnapshot + row.Live, row.SnapshotBytes + row.LiveBytes);
    }
}

/// <summary>
/// Puts an earlier version of a page back (run B.5b, PR 4). A restore is a new edit, not a time machine: it stores an update
/// that replaces the page's content with the version's, gives the page the version's title and file text, and keeps the
/// page as it was in a version of its own (kind <see cref="PageVersionKind.BeforeRestore"/>, protected for a day), so it
/// can be undone the same way. The place in the tree, tags, favorites and to-do fields are not touched, nor is the Joplin
/// import's comparison base: to a later import the page was edited here.
/// <para>
/// Everything that can be done before the locks is: the version is read back (all of it, or the restore is refused), the
/// page is observed as one snapshot, and the replacement is built and checked against it. Then, under the page's lock in the
/// hub (no client's update can be stored meanwhile), one transaction takes the workspace's tree lock, its history lock and
/// the person's role, checks that nothing changed since that observation, and writes everything or nothing. Once it has
/// committed, the hub relays the replacement to everyone who has the page open, as a Sync after a Replaced message, so a
/// client's change that the replacement deletes is kept in a copy.
/// </para>
/// <para>
/// The operation id makes it safe to ask again: a request repeated with the same id gets the first answer, also after the
/// version was pruned; the same id with another page, version or person is refused.
/// </para>
/// </summary>
internal sealed class HistoryRestore(
    InkpactDbContext db,
    IYjsCodec codec,
    HistorySnapshotBuilder builder,
    TreeService tree,
    IPermissionService permissions,
    SyncHub hub,
    SyncOptions sync,
    HistoryPreviewGate gate,
    Compactor compactor,
    TimeProvider time,
    IHistoryRestoreHooks hooks,
    ILogger<HistoryRestore> logger)
{
    /// <summary>How long the version of the page before a restore is kept whatever the retention and the cap say.</summary>
    internal static readonly TimeSpan ProtectFor = TimeSpan.FromHours(24);

    internal async Task<RestoreResult> RestoreAsync(
        Guid pageId, Guid versionId, Guid userId, RestoreRequest request, CancellationToken cancellationToken)
    {
        if (request.OperationId == Guid.Empty || !ContentMark.TryParse(request.ExpectedContent, out var expectedSeq, out var expectedUpdates))
        {
            return RestoreResult.Refused(StatusCodes.Status400BadRequest, "restore_invalid");
        }

        if (await EarlierAsync(request.OperationId, pageId, versionId, userId, cancellationToken) is { } earlier)
        {
            return earlier;
        }

        var version = await db.PageVersions.AsNoTracking()
            .Where(v => v.Id == versionId && v.PageId == pageId)
            .Select(v => new { v.State, v.Title, v.FileText })
            .SingleOrDefaultAsync(cancellationToken);
        var workspaceId = await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => (Guid?)p.WorkspaceId).SingleOrDefaultAsync(cancellationToken);
        if (version is null || workspaceId is null)
        {
            return RestoreResult.Refused(StatusCodes.Status404NotFound, "version_not_found");
        }

        // Asked before the slow part, and again under the locks.
        if (await RefusalAsync(pageId, workspaceId.Value, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        // The cheap look before the sidecar's work: a client whose list is out of date is told so without a replay.
        var stored = await ContentMark.ReadAsync(db, pageId, cancellationToken);
        var metadataVersion = await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.Version).SingleAsync(cancellationToken);
        if (stored.LastSeq != expectedSeq || stored.StoredUpdates != expectedUpdates || metadataVersion != request.ExpectedVersion)
        {
            return RestoreResult.Refused(StatusCodes.Status409Conflict, "page_changed");
        }

        // The replays that follow take the sidecar, which takes one request at a time and people who are editing wait
        // behind: one history read at a time, previews and restores alike.
        Prepared prepared;
        var turn = await gate.EnterAsync(cancellationToken);
        if (turn is null)
        {
            return RestoreResult.Refused(StatusCodes.Status503ServiceUnavailable, "history_unavailable");
        }

        try
        {
            if (await PrepareAsync(pageId, workspaceId.Value, version.State, cancellationToken) is not { } ready)
            {
                return RestoreResult.Refused(StatusCodes.Status409Conflict, "version_unreadable");
            }

            prepared = ready;
        }
        catch (YjsUnavailableException)
        {
            return RestoreResult.Refused(StatusCodes.Status503ServiceUnavailable, "history_unavailable");
        }
        catch (PageUnreadableException)
        {
            return RestoreResult.Refused(StatusCodes.Status409Conflict, "page_unreadable");
        }
        finally
        {
            turn.Dispose();
        }

        if (prepared.MissingAttachments)
        {
            return RestoreResult.Refused(StatusCodes.Status409Conflict, "attachment_missing");
        }

        var current = prepared.Current;
        if (current.ContentSeq != expectedSeq || current.StoredUpdates != expectedUpdates || current.MetadataVersion != request.ExpectedVersion)
        {
            return RestoreResult.Refused(StatusCodes.Status409Conflict, "page_changed");
        }

        // A page that cannot be sent to a client is not made (the sync queue holds twice this; see SyncOptions). Nor one whose
        // stored updates and the replacement together, which is what a client that opens it before its compaction is sent,
        // would not fit three quarters of the queue with the traffic around it: the import's own rule (MaxOldAndNewBytes).
        var together = stored.Bytes + prepared.Replacement.Update.Length;
        if (prepared.Replacement.Update.Length > sync.MaxPageBytes || prepared.After.State.Length > sync.MaxPageBytes
            || together > sync.MaxQueuedBytes / 4 * 3)
        {
            return RestoreResult.Refused(StatusCodes.Status413PayloadTooLarge, "content_too_large");
        }

        await hooks.AtAsync(RestoreStage.Prepared, cancellationToken);

        RestoreResult? outcome = null;
        var published = await hub.PublishAsync(
            pageId,
            prepared.Replacement.Update,
            prepared.Replacement.StateVector,
            async token =>
            {
                outcome = await CommitAsync(pageId, workspaceId.Value, versionId, userId, request.OperationId, version.Title, version.FileText, prepared, token);
                return outcome.Written;
            },
            cancellationToken);

        if (published)
        {
            logger.LogInformation("Restored version {VersionId} of page {PageId}", versionId, pageId);

            // A page whose log is now more than a diff is made of is sent as stored to everyone who opens it: compacted now,
            // as the import does with a large replacement, rather than whenever the worker gets to it. The restore stands
            // whatever happens here; the worker tries again.
            if (together > sync.MaxDiffBytes)
            {
                try
                {
                    await compactor.CompactAsync(pageId, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Page {PageId} was not compacted after its restore: {Reason}", pageId, ex.GetType().Name);
                }
            }
        }

        return outcome ?? throw new InvalidOperationException("The restore's commit ended without an outcome.");
    }

    /// <summary>What the restore was built from: the page as one observation, the version as a document, and the page after it.</summary>
    private sealed record Prepared(HistorySnapshot Current, YjsReplacement Replacement, YjsCompaction After, bool MissingAttachments);

    private sealed class PageUnreadableException : Exception;

    /// <summary>
    /// Reads the version back, checks its files, observes the page and builds the replacement against that observation.
    /// Null when the version cannot be read back whole.
    /// </summary>
    private async Task<Prepared?> PrepareAsync(Guid pageId, Guid workspaceId, byte[] state, CancellationToken cancellationToken)
    {
        YjsRead target;
        try
        {
            target = await codec.ReadAsync([state], cancellationToken);
        }
        catch (YjsRejectedException)
        {
            return null;
        }

        // Something the editor's schema does not have would be left out of the page: not restored in part.
        if (target.Skipped != 0)
        {
            return null;
        }

        // A file is the workspace's: one copied into another page keeps the row of the page it was added to, and the id
        // in the document is all the copy is (see the architecture, "Attachments"). So it counts when it is in a page of
        // this workspace, not only of this page.
        var files = AttachmentIds(target.Json);
        var known = files.Count == 0
            ? 0
            : await db.Attachments.AsNoTracking().CountAsync(
                a => files.Contains(a.Id) && db.Pages.Any(p => p.Id == a.PageId && p.WorkspaceId == workspaceId), cancellationToken);

        HistorySnapshot? current;
        try
        {
            current = await builder.BuildAsync(pageId, cancellationToken);
        }
        catch (Exception ex) when (ex is HistorySnapshotVerificationException or YjsRejectedException)
        {
            throw new PageUnreadableException();
        }

        if (current is null)
        {
            throw new PageUnreadableException();
        }

        // The page's full state (with its first paragraph) stands for its stored inputs: the update holds what it lacks.
        YjsReplacement replacement;
        YjsCompaction after;
        try
        {
            replacement = await codec.ReplaceAsync([current.State], target.Json, cancellationToken);
            after = await codec.CompactAsync([current.State, replacement.Update], cancellationToken);
        }
        catch (YjsRejectedException)
        {
            return null;
        }

        if (after.State.Length == 0
            || !after.ReplayFingerprint.AsSpan().SequenceEqual(after.StateFingerprint)
            || !after.ReplayFingerprint.AsSpan().SequenceEqual(replacement.Fingerprint))
        {
            throw new InvalidOperationException($"The restore of page {pageId} does not read back as what was built.");
        }

        return new Prepared(current, replacement, after, known != files.Count);
    }

    /// <summary>The files a document names (pictures and files in the text), once each.</summary>
    internal static HashSet<Guid> AttachmentIds(byte[] json)
    {
        var ids = new HashSet<Guid>();
        using var document = JsonDocument.Parse(json);
        var pending = new Stack<JsonElement>([document.RootElement]);
        while (pending.TryPop(out var node))
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (node.TryGetProperty("attrs", out var attrs) && attrs.ValueKind == JsonValueKind.Object
                && attrs.TryGetProperty("attachmentId", out var id) && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var attachmentId))
            {
                ids.Add(attachmentId);
            }

            if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    pending.Push(child);
                }
            }
        }

        return ids;
    }

    /// <summary>The answer of a restore made before under this id, or the refusal when the id was used for another.</summary>
    private async Task<RestoreResult?> EarlierAsync(Guid operationId, Guid pageId, Guid versionId, Guid userId, CancellationToken cancellationToken)
    {
        var earlier = await db.PageRestores.AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == operationId, cancellationToken);
        if (earlier is null)
        {
            return null;
        }

        return earlier.PageId == pageId && earlier.VersionId == versionId && earlier.UserId == userId
            ? RestoreResult.Done(new RestoreAnswer(earlier.BeforeVersionId, earlier.RestoreVersionId, earlier.Title, earlier.PageVersion))
            : RestoreResult.Refused(StatusCodes.Status409Conflict, "operation_mismatch");
    }

    /// <summary>Why the page cannot be restored now, whatever the version: it is in the trash, or its workspace keeps no history.</summary>
    private async Task<RestoreResult?> RefusalAsync(Guid pageId, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!await tree.IsLiveAsync(pageId, cancellationToken))
        {
            return RestoreResult.Refused(StatusCodes.Status409Conflict, "page_in_trash");
        }

        // Without history there is nowhere to keep the page as it was.
        var enabled = await db.Workspaces.AsNoTracking().Where(w => w.Id == workspaceId).Select(w => w.HistoryEnabled).SingleAsync(cancellationToken);
        return enabled ? null : RestoreResult.Refused(StatusCodes.Status409Conflict, "history_off");
    }

    private async Task<RestoreResult> CommitAsync(
        Guid pageId,
        Guid workspaceId,
        Guid versionId,
        Guid userId,
        Guid operationId,
        string title,
        string fileText,
        Prepared prepared,
        CancellationToken cancellationToken)
    {
        var current = prepared.Current;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The locks in the order every other writer takes them: the tree's (the workspace row), then the history's, then
        // the person's membership, which is read again here and not only when the request came in.
        if (!await TreeService.LockWorkspaceAsync(db, workspaceId, cancellationToken))
        {
            return RestoreResult.Refused(StatusCodes.Status404NotFound, "version_not_found");
        }

        await WorkspaceHistoryLock.TakeAsync(db, workspaceId, cancellationToken);
        var role = await permissions.LockWorkspaceRoleAsync(userId, workspaceId, cancellationToken);
        await hooks.AtAsync(RestoreStage.Locked, cancellationToken);
        if (role == WorkspaceRole.None)
        {
            return RestoreResult.Refused(StatusCodes.Status404NotFound, "not_found");
        }

        if (role < WorkspaceRole.Editor)
        {
            return RestoreResult.Refused(StatusCodes.Status403Forbidden, "forbidden");
        }

        // Asked again: two requests with one id can both have got past the first look.
        if (await EarlierAsync(operationId, pageId, versionId, userId, cancellationToken) is { } earlier)
        {
            return earlier;
        }

        var page = await db.Pages.SingleOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null || !await db.PageVersions.AnyAsync(v => v.Id == versionId && v.PageId == pageId, cancellationToken))
        {
            return RestoreResult.Refused(StatusCodes.Status404NotFound, "version_not_found");
        }

        if (await RefusalAsync(pageId, workspaceId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        // Exactly the page the replacement was built against: no update stored since (none can be now, the hub holds the
        // page), no compaction that changed what counts (it keeps both numbers), and the same title and place.
        var observed = await ContentMark.ReadAsync(db, pageId, cancellationToken);
        var stored = observed.StoredUpdates;
        if (observed.LastSeq != current.ContentSeq || stored != current.StoredUpdates || page.Version != current.MetadataVersion)
        {
            return RestoreResult.Refused(StatusCodes.Status409Conflict, "page_changed");
        }

        var now = time.GetUtcNow();
        var protectUntil = now + ProtectFor;

        // The page's newest version, tracked: when the page as it is equals it, it becomes the safety version (protected)
        // instead of a copy of the same document, title and file text.
        var newest = await db.PageVersions
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var lastAt = newest?.CreatedAt;
        var newestShown = newest is null ? null : new HistoryCap.Shown(newest.Fingerprint, newest.Title, newest.FileText);
        var currentShown = new HistoryCap.Shown(current.Fingerprint, page.Title, page.FileText);

        // Titles that waited for the capture become versions first, as the capture would have made them (each skipped when
        // it would show the same as the version before it): the history keeps every title the page had before the restore.
        var renames = await db.PageTitleChanges.Where(c => c.PageId == pageId).OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var renameTitles = new List<PageTitleChange>();
        var shownSoFar = newestShown;
        foreach (var rename in renames)
        {
            var shown = new HistoryCap.Shown(current.Fingerprint, rename.Title, current.FileText);
            if (!shown.Same(shownSoFar))
            {
                renameTitles.Add(rename);
                shownSoFar = shown;
            }
        }

        var beforeBytes = PageVersion.PayloadOf(current.State, page.FileText);
        var restoreBytes = PageVersion.PayloadOf(prepared.After.State, fileText);
        // Whether the page as it is would be the last of these versions already: then that version is kept as the safety one.
        bool AlreadyKept(List<PageTitleChange> titles) =>
            currentShown.Same(titles.Count > 0 ? new HistoryCap.Shown(current.Fingerprint, titles[^1].Title, current.FileText) : newestShown);
        long Needed(List<PageTitleChange> titles) =>
            titles.Count * beforeBytes + (AlreadyKept(titles) ? 0 : beforeBytes) + restoreBytes;

        var policy = HistoryPolicy.From(await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId, cancellationToken));
        if (policy.CapBytes is { } cap)
        {
            var (size, kept) = await HistoryCap.MeasureAsync(db, workspaceId, now, cancellationToken);
            // The newest version kept as the safety one becomes protected, and no longer counts as room to make.
            long NowProtected(List<PageTitleChange> titles) =>
                titles.Count == 0 && AlreadyKept(titles) && newest is not null && !(newest.ProtectedUntil > now) ? newest.PayloadBytes : 0;

            // The safety version and the restore must fit; titles that waited are left out, oldest first, when they do not.
            while (renameTitles.Count > 0 && Needed(renameTitles) > cap - kept - NowProtected(renameTitles))
            {
                renameTitles.RemoveAt(0);
            }

            if (Needed(renameTitles) > cap - kept - NowProtected(renameTitles))
            {
                return RestoreResult.Refused(StatusCodes.Status409Conflict, "history_full");
            }

            if (NowProtected(renameTitles) > 0)
            {
                // Before the room is made, so that it is not the version removed to make it.
                newest!.ProtectUntil(protectUntil);
                await db.SaveChangesAsync(cancellationToken);
            }

            await HistoryCap.EvictAsync(db, workspaceId, size + Needed(renameTitles) - cap, now, cancellationToken);
        }

        var renameVersions = renameTitles
            .Select((r, i) => new PageVersion(
                pageId, current.State, r.Title, current.FileText, HistoryCap.After(r.ChangedAt, ref lastAt), r.ChangedBy, PageVersionKind.Rename,
                // The last of them is the safety version when it shows the page as it is.
                protectedUntil: i == renameTitles.Count - 1 && AlreadyKept(renameTitles) ? protectUntil : null,
                fingerprint: current.Fingerprint, contentSeq: current.ContentSeq))
            .ToList();
        PageVersion before;
        if (!AlreadyKept(renameTitles))
        {
            before = new PageVersion(
                pageId, current.State, page.Title, page.FileText, HistoryCap.After(now, ref lastAt), userId, PageVersionKind.BeforeRestore,
                protectedUntil: protectUntil, fingerprint: current.Fingerprint, contentSeq: current.ContentSeq);
            db.PageVersions.Add(before);
        }
        else if (renameVersions.Count > 0)
        {
            before = renameVersions[^1];
        }
        else
        {
            before = newest!;
            before.ProtectUntil(protectUntil);
        }

        var restoreAt = HistoryCap.After(now, ref lastAt);
        db.PageVersions.AddRange(renameVersions);
        db.PageTitleChanges.RemoveRange(renames);
        await db.SaveChangesAsync(cancellationToken);
        await hooks.AtAsync(RestoreStage.BeforeWritten, cancellationToken);

        var update = new PageUpdate { PageId = pageId, UserId = userId, Data = prepared.Replacement.Update, CreatedAt = now };
        db.PageUpdates.Add(update);
        await db.SaveChangesAsync(cancellationToken);
        await hooks.AtAsync(RestoreStage.UpdateStored, cancellationToken);

        var restored = new PageVersion(
            pageId, prepared.After.State, title, fileText, restoreAt, userId, PageVersionKind.Restore,
            fingerprint: prepared.After.ReplayFingerprint, contentSeq: update.Seq);
        db.PageVersions.Add(restored);

        // The title by the tree's rule for a change of title (the tree lock is held): a new metadata version when it changes.
        tree.RetitleUnderLock(page, title);
        page.UpdatedAt = now;
        page.ContentText = prepared.After.Text.Replace("\0", "");
        page.FileText = fileText;
        await LinkIndex.ReplaceAsync(db, pageId, prepared.After.Links, cancellationToken);

        // The page as it is now is in a version (the restore's): the capture has nothing to add.
        var historyState = await db.PageHistoryStates.SingleOrDefaultAsync(s => s.PageId == pageId, cancellationToken);
        if (historyState is null)
        {
            historyState = new PageHistoryState { PageId = pageId };
            db.PageHistoryStates.Add(historyState);
        }

        historyState.CapturedSeq = Math.Max(historyState.CapturedSeq, update.Seq);
        historyState.CapturedUpdates = Math.Max(historyState.CapturedUpdates, stored + 1);
        historyState.FailedAttempts = 0;
        historyState.WaitingAttempts = 0;
        historyState.NextAttemptAt = null;
        historyState.Failure = null;
        historyState.FailedAt = null;

        var answer = new RestoreAnswer(before.Id, restored.Id, page.Title, page.Version);
        db.PageRestores.Add(new PageRestore
        {
            OperationId = operationId,
            PageId = pageId,
            VersionId = versionId,
            UserId = userId,
            BeforeVersionId = before.Id,
            RestoreVersionId = restored.Id,
            Title = page.Title,
            PageVersion = page.Version,
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_page_restores" })
        {
            // Another restore under this id committed meanwhile (of a page in another workspace, under another history lock).
            // Nothing of this one is kept; the answer is the one for that id.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await EarlierAsync(operationId, pageId, versionId, userId, cancellationToken)
                ?? throw new InvalidOperationException("An operation id was taken, and its restore is not found.", ex);
        }

        await hooks.AtAsync(RestoreStage.PageWritten, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RestoreResult.Done(answer) with { Written = true };
    }
}
