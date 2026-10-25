namespace Spike.Server;

/// <summary>
/// Compacts pages that have collected enough live updates. Spike only: it polls on a timer; run 1.10
/// adds the idle-time trigger from the architecture ("500 updates or 10 minutes idle").
/// </summary>
public sealed class CompactionWorker(
    IUpdateStore store,
    Compactor compactor,
    IConfiguration configuration,
    ILogger<CompactionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var threshold = configuration.GetValue("Compaction:Threshold", 500);
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Compaction:IntervalSeconds", 5));
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var pageId in await store.PagesNeedingCompactionAsync(threshold, stoppingToken))
                {
                    await compactor.CompactAsync(pageId, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed round (including a verification failure) changed nothing; try again next tick.
                logger.LogError(ex, "Compaction round failed");
            }
        }
    }
}
