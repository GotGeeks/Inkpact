using System.Collections.Concurrent;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Compaction;

/// <summary>What one round of the worker did.</summary>
public sealed record CompactionRound(int Compacted, int Skipped, int NothingToDo, int Failed, bool SidecarUnavailable);

/// <summary>
/// Looks for pages whose log has grown (a threshold of live updates) or has gone quiet (no new update for a while) and
/// compacts them, one at a time: the Yjs sidecar is a single process, so doing them in parallel would gain nothing.
/// Also empties the archive now and then.
/// </summary>
public sealed class CompactionWorker(
    IDbContextFactory<InkpactDbContext> dbFactory,
    Compactor compactor,
    ArchivePurge purge,
    CompactionOptions options,
    TimeProvider time,
    ILogger<CompactionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);

    // Pages left alone for a while because compacting them failed. In memory: a restart retries each once, which is fine.
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Until, TimeSpan Delay)> _backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.PollInterval, time);
        var nextPurge = time.GetUtcNow() + options.PurgeInterval;
        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                await RunRoundAsync(stoppingToken);
                if (time.GetUtcNow() >= nextPurge)
                {
                    nextPurge = time.GetUtcNow() + options.PurgeInterval;
                    await purge.PurgeAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database was away, say. The next tick tries again.
                logger.LogError(ex, "A compaction round failed");
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
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

    /// <summary>One look at the log and the compactions it leads to. The loop calls it every tick; tests call it directly.</summary>
    public async Task<CompactionRound> RunRoundAsync(CancellationToken cancellationToken)
    {
        int compacted = 0, skipped = 0, nothingToDo = 0, failed = 0;
        var unavailable = false;

        foreach (var pageId in await FindCandidatesAsync(cancellationToken))
        {
            switch (await compactor.CompactAsync(pageId, cancellationToken))
            {
                case CompactionResult.Compacted:
                    compacted++;
                    _backoff.TryRemove(pageId, out _);
                    break;
                case CompactionResult.Skipped:
                    skipped++;
                    break;
                case CompactionResult.NothingToDo:
                    nothingToDo++;
                    break;
                case CompactionResult.Failed:
                    failed++;
                    BackOff(pageId);
                    break;
                case CompactionResult.Unavailable:
                    // The sidecar is restarting, or this page took it down. Either way the rest of the round would meet
                    // the same, and the page is not tried again at once, so a page that always times out cannot keep
                    // the sidecar busy.
                    unavailable = true;
                    BackOff(pageId);
                    break;
            }

            if (unavailable)
            {
                break;
            }
        }

        return new CompactionRound(compacted, skipped, nothingToDo, failed, unavailable);
    }

    /// <summary>Pages with enough live updates, or whose newest one is old, the one waiting longest first.</summary>
    private async Task<List<Guid>> FindCandidatesAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var idleBefore = now - options.IdleAfter;
        var threshold = options.UpdateThreshold;
        var resting = _backoff.Where(b => b.Value.Until > now).Select(b => b.Key).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PageUpdates.AsNoTracking()
            .GroupBy(u => u.PageId)
            .Select(g => new { PageId = g.Key, Count = g.Count(), Newest = g.Max(u => u.CreatedAt) })
            .Where(g => (g.Count >= threshold || g.Newest < idleBefore) && !resting.Contains(g.PageId))
            .OrderBy(g => g.Newest)
            .Select(g => g.PageId)
            .Take(options.PagesPerRound)
            .ToListAsync(cancellationToken);
    }

    private void BackOff(Guid pageId)
    {
        var now = time.GetUtcNow();
        _backoff.AddOrUpdate(
            pageId,
            _ => (now + options.FailureBackoff, options.FailureBackoff),
            (_, previous) =>
            {
                var delay = TimeSpan.FromTicks(Math.Min(previous.Delay.Ticks * 2, MaxBackoff.Ticks));
                return (now + delay, delay);
            });
    }
}
