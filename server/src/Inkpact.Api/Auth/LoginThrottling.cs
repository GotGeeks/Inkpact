using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inkpact.Api.Auth;

public sealed class ThrottleOptions
{
    public const string SectionName = "Auth:Throttle";
    public const string RateLimitPolicy = "auth-attempts";

    /// <summary>Login and setup attempts from one address in a minute.</summary>
    public int PerAddressPerMinute { get; set; } = 10;

    /// <summary>Wrong passwords for one email before it is locked.</summary>
    public int FailuresBeforeLock { get; set; } = 5;

    /// <summary>The first lock; it doubles with every further failure.</summary>
    public TimeSpan FirstLock { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxLock { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>No failure for this long and the count starts again.</summary>
    public TimeSpan ResetAfter { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Slows down guessing passwords for one email address. It treats an address with no account exactly like one with
/// an account, so the lockout tells an attacker nothing about who is registered. The trade-off is that anyone can lock
/// a known address out of new logins for a while; sessions that exist are not affected.
/// </summary>
public sealed class LoginThrottling(InkpactDbContext db, TimeProvider time, ThrottleOptions options)
{
    /// <summary>A hash, so addresses that strangers typed are not kept in the table.</summary>
    public static string KeyFor(string? typedEmail) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((typedEmail ?? "").Trim().ToLowerInvariant())));

    /// <summary>How long this address is still locked, or null when a login may be tried.</summary>
    public async Task<TimeSpan?> GetLockRemainingAsync(string key, CancellationToken cancellationToken)
    {
        var lockedUntil = await db.LoginThrottles.AsNoTracking()
            .Where(t => t.Key == key)
            .Select(t => t.LockedUntil)
            .SingleOrDefaultAsync(cancellationToken);
        var remaining = lockedUntil - time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public async Task RecordFailureAsync(string key, CancellationToken cancellationToken)
    {
        // Two failures at the same moment may both read the same count. The per-address limit bounds what that
        // is worth to an attacker, and fixing it would need a statement EF cannot express.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = time.GetUtcNow();
            var row = await db.LoginThrottles.SingleOrDefaultAsync(t => t.Key == key, cancellationToken);
            var isNew = row is null;
            row ??= new LoginThrottle { Key = key };
            if (!isNew && now - row.LastFailureAt > options.ResetAfter)
            {
                row.Failures = 0;
                row.LockedUntil = null;
            }

            row.Failures++;
            row.LastFailureAt = now;
            if (row.Failures >= options.FailuresBeforeLock)
            {
                row.LockedUntil = now + LockFor(row.Failures);
            }

            if (isNew)
            {
                db.LoginThrottles.Add(row);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Another request created the row first. Look again, and count on top of it.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task ResetAsync(string key, CancellationToken cancellationToken) =>
        await db.LoginThrottles.Where(t => t.Key == key).ExecuteDeleteAsync(cancellationToken);

    /// <summary>One minute at the fifth failure, then double, never more than the maximum.</summary>
    internal TimeSpan LockFor(int failures)
    {
        var doublings = Math.Min(failures - options.FailuresBeforeLock, 20);
        var ticks = options.FirstLock.Ticks * (1L << doublings);
        return TimeSpan.FromTicks(Math.Min(ticks, options.MaxLock.Ticks));
    }
}

public static class ThrottlingSetup
{
    public static IServiceCollection AddLoginThrottling(this IServiceCollection services, IConfiguration config)
    {
        var options = new ThrottleOptions();
        var section = config.GetSection(ThrottleOptions.SectionName);
        if (int.TryParse(section[nameof(ThrottleOptions.PerAddressPerMinute)], out var perAddress)) options.PerAddressPerMinute = perAddress;
        if (int.TryParse(section[nameof(ThrottleOptions.FailuresBeforeLock)], out var failures)) options.FailuresBeforeLock = failures;
        if (int.TryParse(section["FirstLockSeconds"], out var firstLock)) options.FirstLock = TimeSpan.FromSeconds(firstLock);
        if (int.TryParse(section["MaxLockSeconds"], out var maxLock)) options.MaxLock = TimeSpan.FromSeconds(maxLock);

        services.AddSingleton(options);
        services.AddScoped<LoginThrottling>();

        // One address may only try so often, whatever emails it tries. Over the limit: 429 with the wait.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(ThrottleOptions.RateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.PerAddressPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await response.WriteAsJsonAsync(new ApiError("too_many_requests"), cancellationToken);
            };
        });

        return services;
    }
}
