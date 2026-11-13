namespace Inkpact.Api.History;

internal sealed class HistoryOptions
{
    /// <summary>False leaves the retention job out, for tests that run it themselves.</summary>
    public bool RetentionEnabled { get; set; } = true;

    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>False leaves the capture worker out, for tests that run a capture themselves.</summary>
    public bool CaptureEnabled { get; set; } = true;

    /// <summary>How often the worker looks for pages to capture.</summary>
    public TimeSpan CaptureInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The most pages one round captures, one at a time: each is several replays of the page in the sidecar.</summary>
    public int CapturesPerRound { get; set; } = 20;

    /// <summary>A page that was not written to for this long is captured. The compactor's idle time, by default.</summary>
    public TimeSpan CaptureIdleAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A page that keeps being written to is captured once its oldest change that is in no version is this old.</summary>
    public TimeSpan CaptureAtLatestAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A page with more live update bytes than this is left to the compactor first (its own threshold, 2 MiB by default):
    /// after it, a capture reads one snapshot instead of replaying a long log.
    /// </summary>
    public long CaptureMaxLiveBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// How long such a page is left to the compactor. After it the page is captured from its long log: the compactor may
    /// be off, or may not get the page compacted, and the page must not go without versions for that.
    /// </summary>
    public TimeSpan CaptureLargeLogAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a request for a version's content waits for the one before it. One is read at a time: each is a replay in
    /// the sidecar, which takes one request at a time, and people who are editing wait behind it.
    /// </summary>
    public TimeSpan PreviewWait { get; set; } = TimeSpan.FromSeconds(10);
}

public static class HistoryExtensions
{
    public static IServiceCollection AddHistory(this IServiceCollection services, IConfiguration config)
    {
        var options = new HistoryOptions();
        config.GetSection("History").Bind(options);
        if (options.RetentionInterval <= TimeSpan.Zero || options.RetentionInterval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException("History:RetentionInterval must be positive and no longer than the timer supports.");
        }

        if (options.CaptureInterval <= TimeSpan.Zero || options.CaptureInterval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException("History:CaptureInterval must be positive and no longer than the timer supports.");
        }

        if (options.CapturesPerRound < 1 || options.CaptureIdleAfter < TimeSpan.Zero || options.CaptureAtLatestAfter < TimeSpan.Zero
            || options.CaptureMaxLiveBytes < 0 || options.CaptureLargeLogAfter < TimeSpan.Zero || options.PreviewWait < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "History:CapturesPerRound must be 1 or more, and History:CaptureIdleAfter, CaptureAtLatestAfter, CaptureMaxLiveBytes, CaptureLargeLogAfter and PreviewWait not negative.");
        }

        services.AddSingleton(options);
        services.AddScoped<HistoryPolicyService>();
        services.AddSingleton<HistoryRetentionJob>();
        services.AddHostedService<HistoryRetentionWorker>();
        services.AddSingleton<IHistorySnapshotHooks, NoHistorySnapshotHooks>();
        services.AddSingleton<HistorySnapshotBuilder>();
        services.AddSingleton<HistoryCapture>();
        services.AddSingleton<HistoryPreviewGate>();
        services.AddSingleton<IHistoryRestoreHooks, NoHistoryRestoreHooks>();
        services.AddScoped<HistoryRestore>();
        services.AddHostedService<HistoryCaptureWorker>();
        return services;
    }
}
