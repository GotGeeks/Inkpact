using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Auth;

/// <summary>How long the audit log is kept, from the <c>Audit</c> configuration section.</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Events older than this many days are deleted. 0 keeps everything.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>How many events one transaction deletes.</summary>
    public int PurgeBatchSize { get; set; } = 1000;

    /// <summary>How often the job looks. Once a day is plenty for a rule counted in days.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>False leaves the job out, for tests that run it themselves.</summary>
    public bool PurgeEnabled { get; set; } = true;

    public static AuditOptions From(IConfiguration config)
    {
        var options = new AuditOptions();
        config.GetSection(SectionName).Bind(options);
        if (options.RetentionDays < 0)
        {
            throw new InvalidOperationException("Audit:RetentionDays must be 0 (keep everything) or a number of days.");
        }

        if (options.PurgeBatchSize < 1)
        {
            throw new InvalidOperationException("Audit:PurgeBatchSize must be 1 or more.");
        }

        if (options.PurgeInterval <= TimeSpan.Zero || options.PurgeInterval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException("Audit:PurgeInterval must be positive and no longer than the timer supports.");
        }

        return options;
    }
}

/// <summary>
/// Deletes audit events older than the retention, whatever their type. In batches, a batch to a transaction, oldest
/// first: a run that is cut short has deleted whole batches and the next run goes on where it stopped.
/// </summary>
public sealed class AuditRetention(
    IDbContextFactory<InkpactDbContext> dbFactory,
    AuditOptions options,
    TimeProvider time,
    ILogger<AuditRetention> logger)
{
    /// <summary>Deletes the expired events and returns how many went.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        if (options.RetentionDays == 0)
        {
            return 0;
        }

        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.RetentionDays);
        var purged = 0;
        while (true)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var ids = await db.AuditEvents.AsNoTracking()
                .Where(a => a.OccurredAt < cutoff)
                .OrderBy(a => a.OccurredAt)
                .ThenBy(a => a.Id)
                .Select(a => a.Id)
                .Take(options.PurgeBatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                if (purged > 0)
                {
                    logger.LogInformation("Deleted {Count} audit events older than {Days} days", purged, options.RetentionDays);
                }

                return purged;
            }

            // One statement, so one transaction: all of the batch or none of it.
            purged += await db.AuditEvents.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}

internal sealed class AuditRetentionWorker(
    AuditRetention retention,
    AuditOptions options,
    TimeProvider time,
    ILogger<AuditRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.PurgeEnabled || options.RetentionDays == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.PurgeInterval, time);
        do
        {
            try
            {
                await retention.PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deleting old audit events failed; the next round will retry");
            }
        }
        while (await NextTickAsync(timer, stoppingToken));
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

public static class AuditSetup
{
    public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(AuditOptions.From(config));
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddSingleton<AuditRetention>();
        services.AddHostedService<AuditRetentionWorker>();
        return services;
    }
}
