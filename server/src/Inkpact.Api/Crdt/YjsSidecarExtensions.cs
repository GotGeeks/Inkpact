namespace Inkpact.Api.Crdt;

public static class YjsSidecarExtensions
{
    /// <summary>
    /// Registers the sidecar once, as the codec and as the hosted service that keeps its process running. It starts in
    /// the background: the app does not wait for it.
    /// </summary>
    public static IServiceCollection AddYjsSidecar(this IServiceCollection services, IConfiguration config)
    {
        var options = new SidecarOptions();
        config.GetSection(SidecarOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton(sp => new YjsSidecar(
            options,
            sp.GetRequiredService<IHostEnvironment>().ContentRootPath,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<YjsSidecar>>()));
        services.AddSingleton<IYjsCodec>(sp => sp.GetRequiredService<YjsSidecar>());
        services.AddHostedService(sp => sp.GetRequiredService<YjsSidecar>());
        return services;
    }
}
