using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Sync;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Links;

/// <summary>The <c>Links</c> section of the configuration.</summary>
public sealed class LinkIndexOptions
{
    public const string SectionName = "Links";

    /// <summary>Whether the job that indexes the pages that exist from before the index runs at start-up. Tests turn it off and call it themselves.</summary>
    public bool BackfillEnabled { get; set; } = true;

    /// <summary>How many pages one batch indexes.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How long the job waits before it looks again, while the sidecar is down or a page it wanted was being compacted.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many passes in a row may end with something left to do (pages being compacted, a sidecar that is down, an error)
    /// before the job stops until the next start. With the default delay that is about ten minutes.
    /// </summary>
    public int MaxRetries { get; set; } = 20;

    /// <summary>
    /// How often one page may find the sidecar down before the job leaves it until the next start: a page that the sidecar
    /// cannot read in time, or that takes it down, would otherwise take it down again at every pass, and with it everybody's sync.
    /// </summary>
    public int MaxUnavailablePerPage { get; set; } = 3;
}

/// <summary>What one pass of the job did.</summary>
public sealed record BackfillPass(int Indexed, int Skipped, int Failed, bool SidecarUnavailable);

/// <summary>
/// Puts the links of the pages that exist from before the link index into it (run B.1): every page whose <c>links_indexed</c>
/// is below <see cref="LinkIndex.Version"/> and that has no live updates. Such a page is compacted already and nobody edits
/// it, so no compaction would ever index it, though the Joplin import made links in it. A page with live updates is left to
/// the compactor, which indexes it when it compacts it (in batches, with its own care for a page that the sidecar cannot
/// read). So what is read here is at most one snapshot, the size of a page, and only its links are asked for.
/// <para>
/// A page is read and indexed in one transaction, under the compactor's advisory lock for it, so that the two never write the
/// links of one page at once. It runs at start-up, and again after a change to the extraction raised the version.
/// </para>
/// </summary>
public sealed class LinkBackfill(
    IDbContextFactory<InkpactDbContext> dbFactory,
    PageUpdateStore store,
    IYjsCodec codec,
    LinkIndexOptions options,
    SidecarOptions sidecar,
    ILogger<LinkBackfill> logger) : BackgroundService
{
    /// <summary>How often each page found the sidecar down, over the passes of this process.</summary>
    private readonly Dictionary<Guid, int> _unavailable = [];

    /// <summary>Pages that took the sidecar down too often: left until the next start.</summary>
    private readonly HashSet<Guid> _givenUp = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without a sidecar nothing can be read, and nothing would ever be indexed: there is nothing to wait for.
        if (!options.BackfillEnabled || !sidecar.Enabled)
        {
            return;
        }

        try
        {
            for (var troubled = 0; ;)
            {
                BackfillPass? pass = null;
                try
                {
                    pass = await RunPassAsync(stoppingToken);
                    if (pass.Indexed > 0)
                    {
                        logger.LogInformation("Indexed the links of {Count} pages", pass.Indexed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A database that is gone for a moment, say. Never the host's end: the next pass, or the next start, goes on.
                    logger.LogError(ex, "Indexing the links of the pages that exist from before failed; trying again later");
                }

                // Done: nothing left that this process could do. Pages that failed are tried again at the next start.
                if (pass is { SidecarUnavailable: false, Skipped: 0 })
                {
                    return;
                }

                if (++troubled > options.MaxRetries)
                {
                    logger.LogWarning("Indexing the links of the pages that exist from before stops until the next start: it kept finding something in the way");
                    return;
                }

                await Task.Delay(options.RetryDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Indexes the pages that need it, batch after batch, until none is left that it can index. Tests call it directly.</summary>
    public async Task<BackfillPass> RunPassAsync(CancellationToken cancellationToken)
    {
        int indexed = 0, skipped = 0, failed = 0;
        // Pages this pass could not index (being compacted, failed): not asked for again in the same pass.
        var left = new HashSet<Guid>(_givenUp);

        while (true)
        {
            List<Guid> batch;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                batch = await db.Pages.AsNoTracking()
                    .Where(p => p.LinksIndexed < LinkIndex.Version
                        && !db.PageUpdates.Any(u => u.PageId == p.Id)
                        && !left.Contains(p.Id))
                    .OrderBy(p => p.Id)
                    .Select(p => p.Id)
                    .Take(options.BatchSize)
                    .ToListAsync(cancellationToken);
            }

            if (batch.Count == 0)
            {
                return new BackfillPass(indexed, skipped, failed, false);
            }

            foreach (var pageId in batch)
            {
                try
                {
                    if (await IndexAsync(pageId, cancellationToken))
                    {
                        indexed++;
                    }
                    else
                    {
                        skipped++;
                        left.Add(pageId);
                    }
                }
                catch (YjsUnavailableException ex)
                {
                    // The sidecar is down, or this page took it down (too large to read in time, or out of memory). Either way
                    // the pass ends; the page is left for good when it happens to it again and again.
                    var times = _unavailable[pageId] = _unavailable.GetValueOrDefault(pageId) + 1;
                    if (times >= options.MaxUnavailablePerPage && _givenUp.Add(pageId))
                    {
                        logger.LogWarning(ex, "The links of page {PageId} are left until the next start: the sidecar was not available for it {Times} times", pageId, times);
                    }
                    else
                    {
                        logger.LogDebug(ex, "Indexing links waits: the Yjs sidecar is not available");
                    }

                    return new BackfillPass(indexed, skipped, failed, true);
                }
                catch (YjsRejectedException ex)
                {
                    logger.LogError(ex, "The links of page {PageId} cannot be indexed: Yjs cannot decode its snapshot", pageId);
                    failed++;
                    left.Add(pageId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The page was deleted meanwhile (its rows would have no page), or the database went away for a moment.
                    logger.LogWarning(ex, "The links of page {PageId} could not be indexed in this pass", pageId);
                    failed++;
                    left.Add(pageId);
                }
            }
        }
    }

    /// <summary>
    /// Indexes one page. False when it is not for this job at this moment: the compactor has it, or it got live updates since it
    /// was picked, and the compactor indexes it when it compacts it.
    /// </summary>
    private async Task<bool> IndexAsync(Guid pageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await Compactor.TryLockPageAsync(db, pageId, cancellationToken))
        {
            return false;
        }

        // After the lock: a compaction that finished meanwhile has indexed the page, and this finds it so.
        if (await db.Pages.AsNoTracking().AnyAsync(p => p.Id == pageId && p.LinksIndexed >= LinkIndex.Version, cancellationToken))
        {
            return true;
        }

        var content = await store.LoadAsync(pageId, cancellationToken);
        if (content.Updates.Count > 0)
        {
            return false;
        }

        var links = content.Snapshot is { } snapshot ? await codec.LinksAsync([snapshot], cancellationToken) : [];
        await LinkIndex.ReplaceAsync(db, pageId, links, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
