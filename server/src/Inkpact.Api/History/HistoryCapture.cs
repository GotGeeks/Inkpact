using Inkpact.Api.Crdt;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

internal enum HistoryCaptureOutcome
{
    /// <summary>At least one version was written.</summary>
    Captured,

    /// <summary>Nothing differs from the page's newest version; only the cursor moved.</summary>
    Unchanged,

    /// <summary>The workspace's history is off: nothing was written, and titles that waited are dropped.</summary>
    Off,

    /// <summary>The workspace's cap leaves no room, also after the oldest unprotected versions went.</summary>
    Full,

    /// <summary>The stored content cannot be read back as the document it is.</summary>
    Unreadable,

    /// <summary>The Yjs sidecar did not answer. Nothing is wrong with the page.</summary>
    SidecarUnavailable,

    /// <summary>The page or its workspace no longer exists.</summary>
    Gone,
}

/// <summary>
/// Makes versions of pages whose stored content, or title, is not in a version yet (run B.5b). Nothing on the edit path
/// knows about it: what is still to capture is worked out from the stored updates and each page's cursor
/// (<see cref="PageHistoryState"/>), and a rename leaves a row (<see cref="PageTitleChange"/>).
/// <para>
/// A version is written in one transaction under the workspace's history lock, with the policy read again inside it, so
/// a capture never adds a version after history was turned off, and never passes the cap. A page that changed while it
/// was captured simply has something to capture again: the cursor is what the capture observed, not what is there now.
/// </para>
/// </summary>
internal sealed class HistoryCapture(
    IDbContextFactory<InkpactDbContext> dbFactory,
    HistorySnapshotBuilder builder,
    HistoryOptions options,
    TimeProvider time,
    ILogger<HistoryCapture> logger)
{
    private static readonly TimeSpan MaxUnreadableBackoff = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxUnavailableBackoff = TimeSpan.FromHours(1);

    /// <summary>Captures the pages that are due, oldest change first, one at a time. Returns how many it looked at.</summary>
    internal async Task<int> RunRoundAsync(CancellationToken cancellationToken)
    {
        await DropTitlesWithoutHistoryAsync(cancellationToken);
        var pages = await DuePagesAsync(cancellationToken);
        var done = 0;
        foreach (var pageId in pages)
        {
            HistoryCaptureOutcome outcome;
            try
            {
                outcome = await CaptureAsync(pageId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Anything else that goes wrong with one page (the database, a row nobody foresaw) must not keep the
                // pages behind it waiting: the page is left alone for a while, as when the sidecar is down, and the
                // round goes on. The exception's type only: its message may quote what it could not handle.
                logger.LogError("The capture of page {PageId} failed: {Reason}", pageId, ex.GetType().Name);
                await WaitAsync(pageId, cancellationToken);
                done++;
                continue;
            }

            done++;
            if (outcome == HistoryCaptureOutcome.SidecarUnavailable)
            {
                // The others would find it down too. The page that met it waits a little longer than the rest, so one
                // page that takes the sidecar down every time does not keep the head of the queue.
                break;
            }
        }

        return done;
    }

    /// <summary>
    /// A title that was given while the workspace's history is off gets no version: the rename leaves its row without
    /// asking, so that the rule is here alone, and this removes it. Also the rows that waited when history was turned off.
    /// </summary>
    internal async Task<int> DropTitlesWithoutHistoryAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PageTitleChanges
            .Where(c => db.Pages.Any(p => p.Id == c.PageId && db.Workspaces.Any(w => w.Id == p.WorkspaceId && !w.HistoryEnabled)))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The pages with something to capture that are due. A page has something to capture when it was given more updates
    /// than its newest version saw (<see cref="PageHistoryState.CapturedUpdates"/>): counted, not compared by sequence
    /// number, because an update can commit late with a number below one that is already captured. Or when a title waits.
    /// <para>
    /// Due is: idle (the newest live update is older than <see cref="HistoryOptions.CaptureIdleAfter"/>), or changing for
    /// long (the oldest update that is in no version is older than <see cref="HistoryOptions.CaptureAtLatestAfter"/>), or
    /// renamed. The oldest update is looked for in the archive too: the compactor moves the updates of a page that is
    /// typed in without a pause there every few minutes, and the live ones alone would always look young.
    /// </para>
    /// <para>
    /// Not due: a page that failed and is still waiting, unless it is one that could not be read and was written to since;
    /// and, for <see cref="HistoryOptions.CaptureLargeLogAfter"/>, a page with so many live updates that the compactor
    /// should get to it first.
    /// </para>
    /// <para>
    /// Only pages that can have something are looked at (a live update, a waiting title, or a snapshot with more updates
    /// than were captured), so a library in which nothing changed costs three index reads, not a row per page.
    /// </para>
    /// </summary>
    internal async Task<List<Guid>> DuePagesAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var idleBefore = now - options.CaptureIdleAfter;
        var longBefore = now - options.CaptureAtLatestAfter;
        var largeBefore = now - options.CaptureLargeLogAfter;
        var maxLiveBytes = options.CaptureMaxLiveBytes;
        var unreadable = PageHistoryFailures.Unreadable;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = db.PageUpdates.Select(u => u.PageId)
            .Union(db.PageTitleChanges.Select(c => c.PageId))
            .Union(db.PageSnapshots
                .Where(s => s.UpdateCount > (db.PageHistoryStates.Where(h => h.PageId == s.PageId).Select(h => (long?)h.CapturedUpdates).FirstOrDefault() ?? 0))
                .Select(s => s.PageId));

        var pages =
            from p in db.Pages.AsNoTracking()
            where candidates.Contains(p.Id) && db.Workspaces.Any(w => w.Id == p.WorkspaceId && w.HistoryEnabled)
            from s in db.PageHistoryStates.Where(s => s.PageId == p.Id).DefaultIfEmpty()
            select new
            {
                p.Id,
                Captured = (long?)s.CapturedSeq ?? 0,
                CapturedUpdates = (long?)s.CapturedUpdates ?? 0,
                NextAttemptAt = (DateTimeOffset?)s.NextAttemptAt,
                s.Failure,
                FailedAt = (DateTimeOffset?)s.FailedAt,
                InSnapshot = db.PageSnapshots.Where(x => x.PageId == p.Id).Select(x => (long?)x.UpdateCount).FirstOrDefault() ?? 0,
                SnapshotAt = db.PageSnapshots.Where(x => x.PageId == p.Id).Select(x => (DateTimeOffset?)x.UpdatedAt).FirstOrDefault(),
                Live = db.PageUpdates.LongCount(u => u.PageId == p.Id),
                NewestLiveAt = db.PageUpdates.Where(u => u.PageId == p.Id).Max(u => (DateTimeOffset?)u.CreatedAt),
                LiveBytes = db.PageUpdates.Where(u => u.PageId == p.Id).Sum(u => (long?)u.Data.Length) ?? 0,
                RenamedAt = db.PageTitleChanges.Where(c => c.PageId == p.Id).Min(c => (DateTimeOffset?)c.ChangedAt),
            };

        return await pages
            .Select(x => new
            {
                Page = x,
                OldestLive = db.PageUpdates.Where(u => u.PageId == x.Id && u.Seq > x.Captured).Min(u => (DateTimeOffset?)u.CreatedAt),
                OldestArchived = db.ArchivedPageUpdates.Where(u => u.PageId == x.Id && u.Seq > x.Captured).Min(u => (DateTimeOffset?)u.CreatedAt),
            })
            .Select(x => new
            {
                x.Page,
                OldestAt = x.OldestLive == null ? x.OldestArchived
                    : x.OldestArchived == null || x.OldestLive < x.OldestArchived ? x.OldestLive : x.OldestArchived,
            })
            .Where(x =>
                // Waiting after a failure, unless the page could not be read and was written to since (not more often
                // than a page that is typed in is captured).
                (x.Page.NextAttemptAt == null || x.Page.NextAttemptAt <= now
                    || (x.Page.Failure == unreadable && x.Page.FailedAt <= longBefore
                        && (x.Page.NewestLiveAt > x.Page.FailedAt || x.Page.SnapshotAt > x.Page.FailedAt)))
                // A long log is the compactor's first, for a while.
                && (x.Page.LiveBytes <= maxLiveBytes
                    || (x.OldestAt == null && x.Page.RenamedAt == null) || x.OldestAt <= largeBefore || x.Page.RenamedAt <= largeBefore)
                && (x.Page.RenamedAt != null
                    || (x.Page.InSnapshot + x.Page.Live > x.Page.CapturedUpdates
                        && (x.Page.NewestLiveAt == null || x.Page.NewestLiveAt <= idleBefore || x.OldestAt <= longBefore))))
            // A page whose changes carry no time any more (all compacted, the archive purged) has waited longest.
            .OrderBy(x => x.OldestAt ?? x.Page.RenamedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Page.Id)
            .Select(x => x.Page.Id)
            .Take(options.CapturesPerRound)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Captures one page now, whether it is due or not.</summary>
    internal async Task<HistoryCaptureOutcome> CaptureAsync(Guid pageId, CancellationToken cancellationToken)
    {
        HistorySnapshot? snapshot;
        try
        {
            snapshot = await builder.BuildAsync(pageId, cancellationToken);
        }
        catch (YjsUnavailableException)
        {
            await WaitAsync(pageId, cancellationToken);
            return HistoryCaptureOutcome.SidecarUnavailable;
        }
        catch (Exception ex) when (ex is HistorySnapshotVerificationException or YjsRejectedException)
        {
            // The message of these names the page and the reason, never its content.
            logger.LogWarning("No version could be made of page {PageId}: {Reason}", pageId, ex.GetType().Name);
            await FailAsync(pageId, PageHistoryFailures.Unreadable, TimeSpan.FromHours(1), MaxUnreadableBackoff, cancellationToken);
            return HistoryCaptureOutcome.Unreadable;
        }

        if (snapshot is null)
        {
            return HistoryCaptureOutcome.Gone;
        }

        var outcome = await CommitAsync(snapshot, cancellationToken);
        if (outcome == HistoryCaptureOutcome.Full)
        {
            logger.LogWarning("No version was made of page {PageId}: the history of workspace {WorkspaceId} is at its cap", pageId, snapshot.WorkspaceId);
            await FailAsync(pageId, PageHistoryFailures.Full, TimeSpan.FromHours(1), TimeSpan.FromHours(1), cancellationToken);
        }

        return outcome;
    }

    private async Task<HistoryCaptureOutcome> CommitAsync(HistorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var pageId = snapshot.PageId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await WorkspaceHistoryLock.TryTakeAsync(db, snapshot.WorkspaceId, cancellationToken)
            || !await db.Pages.AnyAsync(p => p.Id == pageId && p.WorkspaceId == snapshot.WorkspaceId, cancellationToken))
        {
            return HistoryCaptureOutcome.Gone;
        }

        // The policy as it is now, not as it was when the page was chosen.
        var policy = HistoryPolicy.From(await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == snapshot.WorkspaceId, cancellationToken));

        // The renames this observation saw. One that committed after it waits for the next capture, whose body is later.
        var renames = await db.PageTitleChanges
            .Where(c => c.PageId == pageId && c.PageVersion <= snapshot.MetadataVersion)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);

        if (!policy.Enabled)
        {
            db.PageTitleChanges.RemoveRange(renames);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return HistoryCaptureOutcome.Off;
        }

        var newest = await db.PageVersions.AsNoTracking()
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.CreatedAt)
            .ThenByDescending(v => v.Id)
            .Select(v => new { v.Fingerprint, v.Title, v.FileText, v.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var lastAt = newest?.CreatedAt;
        var last = newest is null ? null : new Shown(newest.Fingerprint, newest.Title, newest.FileText);

        var versions = new List<PageVersion>();
        foreach (var rename in renames)
        {
            var shown = new Shown(snapshot.Fingerprint, rename.Title, snapshot.FileText);
            if (!shown.Same(last))
            {
                versions.Add(new PageVersion(
                    pageId, snapshot.State, rename.Title, snapshot.FileText, HistoryCap.After(rename.ChangedAt, ref lastAt), rename.ChangedBy, PageVersionKind.Rename,
                    fingerprint: snapshot.Fingerprint, contentSeq: snapshot.ContentSeq));
                last = shown;
            }
        }

        var current = new Shown(snapshot.Fingerprint, snapshot.Title, snapshot.FileText);
        if (!current.Same(last))
        {
            // Nobody in particular: an automatic version can hold several people's edits.
            versions.Add(new PageVersion(
                pageId, snapshot.State, snapshot.Title, snapshot.FileText, HistoryCap.After(snapshot.ObservedAt, ref lastAt), createdBy: null, PageVersionKind.Automatic,
                fingerprint: snapshot.Fingerprint, contentSeq: snapshot.ContentSeq));
        }

        if (versions.Count > 0 && policy.CapBytes is { } cap)
        {
            var now = time.GetUtcNow();
            var (size, kept) = await HistoryCap.MeasureAsync(db, snapshot.WorkspaceId, now, cancellationToken);

            // Under a cap the oldest versions go first, and that holds for the ones about to be written too: when the
            // titles that waited and the page as it is do not all fit, the earliest titles are left out. Each of them is
            // a full copy of the page, so a few renames of a large page would otherwise never fit, and the page would get
            // no version again.
            while (versions.Count > 1 && versions.Sum(v => v.PayloadBytes) > cap - kept)
            {
                versions.RemoveAt(0);
            }

            var needed = versions.Sum(v => v.PayloadBytes);
            if (needed > cap - kept)
            {
                await transaction.RollbackAsync(cancellationToken);
                return HistoryCaptureOutcome.Full;
            }

            await HistoryCap.EvictAsync(db, snapshot.WorkspaceId, size + needed - cap, now, cancellationToken);
        }

        db.PageVersions.AddRange(versions);
        db.PageTitleChanges.RemoveRange(renames);
        var state = await db.PageHistoryStates.SingleOrDefaultAsync(s => s.PageId == pageId, cancellationToken);
        if (state is null)
        {
            state = new PageHistoryState { PageId = pageId };
            db.PageHistoryStates.Add(state);
        }

        // What this capture observed. Content stored since has a higher sequence number and is captured next time.
        state.CapturedSeq = Math.Max(state.CapturedSeq, snapshot.ContentSeq);
        state.CapturedUpdates = Math.Max(state.CapturedUpdates, snapshot.StoredUpdates);
        state.FailedAttempts = 0;
        state.WaitingAttempts = 0;
        state.NextAttemptAt = null;
        state.Failure = null;
        state.FailedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return versions.Count > 0 ? HistoryCaptureOutcome.Captured : HistoryCaptureOutcome.Unchanged;
    }

    /// <summary>What makes two versions of a page the same to someone looking at them.</summary>
    private sealed record Shown(byte[] Fingerprint, string Title, string FileText)
    {
        public bool Same(Shown? other) =>
            other is not null && Title == other.Title && FileText == other.FileText
            && Fingerprint.Length > 0 && Fingerprint.AsSpan().SequenceEqual(other.Fingerprint);
    }

    /// <summary>
    /// Leaves the page alone for a while because something that is not the page's own went wrong (the sidecar did not
    /// answer, or an error nobody foresaw): a minute, twice as long after each time in a row, an hour at most. What is
    /// known about the page itself stays: a page that could not be read is still that, and its own count is not raised.
    /// </summary>
    private async Task WaitAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await NoteAsync(
            pageId,
            state =>
            {
                state.WaitingAttempts++;
                var until = time.GetUtcNow() + Backoff(TimeSpan.FromMinutes(1), MaxUnavailableBackoff, state.WaitingAttempts);
                state.NextAttemptAt = state.NextAttemptAt is { } already && already > until ? already : until;
            },
            cancellationToken);
    }

    /// <summary>
    /// Notes why no version could be made of the page, and leaves it alone for a while: twice as long after each time in
    /// a row for the same reason. The cursor stays where it was.
    /// </summary>
    private async Task FailAsync(Guid pageId, string failure, TimeSpan first, TimeSpan longest, CancellationToken cancellationToken)
    {
        await NoteAsync(
            pageId,
            state =>
            {
                state.FailedAttempts = state.Failure == failure ? state.FailedAttempts + 1 : 1;
                state.Failure = failure;
                state.FailedAt = time.GetUtcNow();
                state.NextAttemptAt = state.FailedAt + Backoff(first, longest, state.FailedAttempts);
            },
            cancellationToken);
    }

    private static TimeSpan Backoff(TimeSpan first, TimeSpan longest, int attempts) =>
        TimeSpan.FromTicks(Math.Min(first.Ticks * (1L << Math.Clamp(attempts - 1, 0, 20)), longest.Ticks));

    private async Task NoteAsync(Guid pageId, Action<PageHistoryState> note, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var state = await db.PageHistoryStates.SingleOrDefaultAsync(s => s.PageId == pageId, cancellationToken);
            if (state is null)
            {
                state = new PageHistoryState { PageId = pageId };
                db.PageHistoryStates.Add(state);
            }

            note(state);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The page was deleted meanwhile: there is nothing left to wait for.
        }
    }
}

internal sealed class HistoryCaptureWorker(HistoryCapture capture, HistoryOptions options, TimeProvider time, ILogger<HistoryCaptureWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CaptureEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.CaptureInterval, time);
        while (await NextTickAsync(timer, stoppingToken))
        {
            try
            {
                await capture.RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A round of history capture failed; the next round tries again");
            }
        }
    }

    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
