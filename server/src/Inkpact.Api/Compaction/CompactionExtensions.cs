using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inkpact.Api.Compaction;

public static class CompactionExtensions
{
    public static IServiceCollection AddCompaction(this IServiceCollection services, IConfiguration config)
    {
        var options = new CompactionOptions();
        config.GetSection(CompactionOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.TryAddSingleton<ICompactionHooks, NoCompactionHooks>();
        services.AddSingleton<Compactor>();
        return services;
    }
}
