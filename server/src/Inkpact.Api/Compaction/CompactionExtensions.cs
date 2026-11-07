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
        services.AddSingleton<ArchivePurge>();
        services.AddHostedService<CompactionWorker>();

        // The links of the pages that exist from before the link index (run B.1).
        var links = new Links.LinkIndexOptions();
        config.GetSection(Links.LinkIndexOptions.SectionName).Bind(links);
        services.AddSingleton(links);
        services.AddHostedService<Links.LinkBackfill>();
        return services;
    }
}
