namespace Inkpact.Api.History;

internal sealed class HistoryOptions
{
    public bool RetentionEnabled { get; set; } = true;

    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);
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

        services.AddSingleton(options);
        services.AddScoped<HistoryPolicyService>();
        services.AddSingleton<HistoryRetentionJob>();
        services.AddHostedService<HistoryRetentionWorker>();
        return services;
    }
}
