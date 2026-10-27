using System.Threading.Channels;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Import;

/// <summary>The imports waiting to run, in the order they were uploaded. In memory: a restart marks what was waiting as interrupted.</summary>
public sealed class ImportQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid importId) => _channel.Writer.TryWrite(importId);

    public ChannelReader<Guid> Reader => _channel.Reader;
}

/// <summary>
/// Runs the imports one at a time, whoever started them: they are heavy, and the Yjs sidecar is one process. At start-up
/// it deals with what an earlier run of the app left: an import that was waiting or running is marked as interrupted (the
/// person uploads the file again, and what was already imported is skipped), and files nobody will read are removed.
/// </summary>
public sealed class ImportWorker(
    ImportQueue queue,
    ImportRunner runner,
    IServiceScopeFactory scopes,
    ImportOptions options,
    TimeProvider time,
    ILogger<ImportWorker> logger) : BackgroundService
{
    /// <summary>A file this old, with no import to read it, is a leftover (an upload that was cut off).</summary>
    private static readonly TimeSpan StrayFileAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cleans up before the app takes its first request: the host waits for this, so an upload can never be taken for
    /// something an earlier run left.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RecoverAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The imports of the earlier run stay as they were, and are not retried.
            logger.LogError(e, "The imports of an earlier run could not be cleaned up");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var importId in queue.Reader.ReadAllAsync(stoppingToken).WithCancellation(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await runner.RunAsync(importId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The runner reports what goes wrong with an import in the import. This is what it could not.
                logger.LogError(e, "Import {ImportId} stopped unexpectedly", importId);
            }
        }
    }

    /// <summary>Marks the imports the last run of the app did not finish, and removes the files that are no use.</summary>
    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var now = time.GetUtcNow();
            var interrupted = await db.Imports
                .Where(i => i.State == ImportStates.Queued || i.State == ImportStates.Running)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(i => i.State, ImportStates.Failed)
                        .SetProperty(i => i.ErrorCode, "interrupted")
                        .SetProperty(i => i.FinishedAt, now),
                    cancellationToken);
            if (interrupted > 0)
            {
                logger.LogWarning("{Count} imports were interrupted by a restart; uploading the file again finishes them", interrupted);
            }
        }

        if (!Directory.Exists(options.Directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(options.Directory, "*.jex"))
        {
            try
            {
                if (time.GetUtcNow() - File.GetLastWriteTimeUtc(file) > StrayFileAge)
                {
                    File.Delete(file);
                }
            }
            catch (IOException e)
            {
                logger.LogWarning(e, "The old import file {File} could not be removed", file);
            }
        }
    }
}
