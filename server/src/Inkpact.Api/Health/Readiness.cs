using Inkpact.Api.Crdt;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Health;

/// <summary>What <c>GET /api/health/ready</c> answers: one word for the whole, and one per part. Nothing else about the server.</summary>
public sealed record ReadinessReport(string Status, ReadinessChecks Checks)
{
    public const string Ok = "ok";

    /// <summary>The app works, but something waits: without the sidecar that is compaction, search text and imports.</summary>
    public const string Degraded = "degraded";

    /// <summary>The app cannot do its work: the database does not answer, or is not the version this app needs.</summary>
    public const string Unhealthy = "unhealthy";

    public const string Unavailable = "unavailable";
    public const string MigrationPending = "migration_pending";
}

public sealed record ReadinessChecks(string Database, string Sidecar);

/// <summary>
/// Whether the app can do its work right now: the database (it answers, and has no migration waiting) and the Yjs
/// sidecar (a process that passed its start-up check and is still there). The endpoint is anonymous, so the answer is
/// kept for a few seconds: asking it over and over reaches the database once per <see cref="CacheFor"/>.
/// <para>
/// The sidecar is not sent a request: it takes one at a time, so a check would wait behind a long compaction and call a
/// busy sidecar unavailable, and a call that times out is how a stuck sidecar gets killed.
/// </para>
/// </summary>
public sealed class Readiness(IServiceScopeFactory scopes, YjsSidecar sidecar, TimeProvider time, ILogger<Readiness> logger)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Answer? _last;

    private sealed record Answer(ReadinessReport Report, DateTimeOffset At);

    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken)
    {
        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have asked while this one waited.
            if (Fresh() is { } justMade)
            {
                return justMade;
            }

            var database = await DatabaseAsync();
            var sidecarState = sidecar.IsReady ? ReadinessReport.Ok : ReadinessReport.Unavailable;
            var status = database != ReadinessReport.Ok ? ReadinessReport.Unhealthy
                : sidecarState != ReadinessReport.Ok ? ReadinessReport.Degraded
                : ReadinessReport.Ok;

            var report = new ReadinessReport(status, new ReadinessChecks(database, sidecarState));
            _last = new Answer(report, time.GetUtcNow());
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ReadinessReport? Fresh() =>
        _last is { } last && time.GetUtcNow() - last.At < CacheFor ? last.Report : null;

    private async Task<string> DatabaseAsync()
    {
        // Its own time limit, not the caller's: a caller that goes away must not leave a half answer in the cache.
        using var timeout = new CancellationTokenSource(DatabaseTimeout);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            // Opened first, so that a database that is not there fails here. Asked for its migrations alone, a missing
            // database reads as "no migration applied yet", which would answer migration_pending for a database that is gone.
            await db.Database.OpenConnectionAsync(timeout.Token);
            var pending = await db.Database.GetPendingMigrationsAsync(timeout.Token);
            return pending.Any() ? ReadinessReport.MigrationPending : ReadinessReport.Ok;
        }
        catch (Exception exception)
        {
            // The reason goes to the log, which the person who runs the app reads; the anonymous answer has one word.
            logger.LogWarning("The readiness check could not reach the database: {Reason}", exception.GetType().Name);
            return ReadinessReport.Unavailable;
        }
    }
}

public static class HealthEndpoints
{
    public static IServiceCollection AddHealth(this IServiceCollection services) => services.AddSingleton<Readiness>();

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Is the process there? Reads nothing, so it answers while the database is down. Anonymous: the container's
        // own check and a proxy call it without a session.
        app.MapGet("/api/health", () => Results.Ok(new { status = ReadinessReport.Ok })).AllowAnonymous();

        // Can it do its work? Anonymous for the same callers; the answer is three words and says nothing else.
        app.MapGet("/api/health/ready", async (Readiness readiness, CancellationToken cancellationToken) =>
        {
            var report = await readiness.CheckAsync(cancellationToken);
            return Results.Json(report, statusCode: report.Status == ReadinessReport.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK);
        }).AllowAnonymous();
        return app;
    }
}
