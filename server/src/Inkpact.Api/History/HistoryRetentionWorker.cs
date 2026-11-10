using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.History;

internal sealed record HistoryRetentionRound(int Processed, int Failed);

internal sealed class HistoryRetentionWorker(
    IDbContextFactory<InkpactDbContext> dbFactory,
    HistoryRetentionJob job,
    HistoryOptions options,
    TimeProvider time,
    ILogger<HistoryRetentionWorker> logger) : BackgroundService
{
    private const int WorkspaceBatchSize = 64;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.RetentionEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.RetentionInterval, time);
        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                await RunRoundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A history retention round failed; the next round will retry");
            }
        }
    }

    internal async Task<HistoryRetentionRound> RunRoundAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        var failed = 0;
        Guid? after = null;
        while (true)
        {
            var workspaceIds = await WorkspaceIdsAsync(after, cancellationToken);
            if (workspaceIds.Count == 0)
            {
                return new(processed, failed);
            }

            foreach (var workspaceId in workspaceIds)
            {
                try
                {
                    await job.PruneAsync(workspaceId, cancellationToken);
                    processed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogError(ex, "History retention failed for workspace {WorkspaceId}; the next round will retry", workspaceId);
                }
            }

            after = workspaceIds[^1];
        }
    }

    private async Task<List<Guid>> WorkspaceIdsAsync(Guid? after, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var workspaces = db.Workspaces.AsNoTracking().Where(w =>
            db.Pages.Any(p => p.WorkspaceId == w.Id && db.PageVersions.Any(v => v.PageId == p.Id)));
        if (after is { } last)
        {
            workspaces = workspaces.Where(w => w.Id.CompareTo(last) > 0);
        }

        return await workspaces.OrderBy(w => w.Id).Select(w => w.Id).Take(WorkspaceBatchSize).ToListAsync(cancellationToken);
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
}
