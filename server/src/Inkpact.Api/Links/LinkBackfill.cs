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

    /// <summary>How many times in a row the job looks again for pages it could not index (they were being compacted) before it leaves them to the compactor.</summary>
    public int MaxRetries { get; set; } = 10;
}

/// <summary>What one pass of the job did.</summary>
public sealed record BackfillPass(int Indexed, int Skipped, int Failed, bool SidecarUnavailable);

/// <summary>
/// Puts the links of the pages that exist from before the link index into it (run B.1): every page whose <c>links_indexed</c>
/// is below <see cref="LinkIndex.Version"/>. Without it, a page that is never compacted again (nobody edits it) would have no
/// backlinks to show, though the Joplin import made links in it. It runs at start-up, and again after a change to the
/// extraction raised the version; pages made after that are indexed by their first compaction.
/// <para>
/// A page is read and indexed in one transaction, under the compactor's advisory lock for it, so that the two never write the
/// links of one page at once. A page that is being compacted is left to the compactor, which indexes it itself. Nothing is
/// compacted here: the state the sidecar merges is only read.
/// </para>
/// </summary>
public sealed class LinkBackfill(
    IDbContextFactory<InkpactDbContext> dbFactory,
    PageUpdateStore store,
    IYjsCodec codec,
    LinkIndexOptions options,
    ILogger<LinkBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.BackfillEnabled)
        {
            return;
        }

        try
        {
            var retries = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var pass = await RunPassAsync(stoppingToken);
                if (pass.Indexed > 0)
                {
                    logger.LogInformation("Indexed the links of {Count} pages", pass.Indexed);
                }

                // Nothing left that this pass could do: the pages that failed are not tried again until the next start.
                if (!pass.SidecarUnavailable && pass.Skipped == 0)
                {
                    return;
                }

                if (!pass.SidecarUnavailable && ++retries > options.MaxRetries)
                {
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

    /// <summary>Indexes the pages below the current version, batch after batch, until none is left that it can index. Tests call it directly.</summary>
    public async Task<BackfillPass> RunPassAsync(CancellationToken cancellationToken)
    {
        int indexed = 0, skipped = 0, failed = 0;
        // Pages this pass could not index (being compacted, or not decodable): not asked for again in the same pass.
        var left = new HashSet<Guid>();

        while (true)
        {
            List<Guid> batch;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
            {
                batch = await db.Pages.AsNoTracking()
                    .Where(p => p.LinksIndexed < LinkIndex.Version && !left.Contains(p.Id))
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
                    logger.LogDebug(ex, "Indexing links waits: the Yjs sidecar is not available");
                    return new BackfillPass(indexed, skipped, failed, true);
                }
                catch (YjsRejectedException ex)
                {
                    logger.LogError(ex, "The links of page {PageId} cannot be indexed: Yjs cannot decode one of its updates", pageId);
                    failed++;
                    left.Add(pageId);
                }
            }
        }
    }

    /// <summary>Indexes one page. False when the compactor has it at this moment (it indexes it itself).</summary>
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
        var links = content.Inputs.Count == 0 ? [] : (await codec.CompactAsync(content.Inputs, cancellationToken)).Links;
        await LinkIndex.ReplaceAsync(db, pageId, links, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
