using Inkpact.Api.Crdt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inkpact.Api.Tests.Testing;

/// <summary>Sidecars for tests that use one directly, without the app around it.</summary>
internal static class YjsSidecars
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The real sidecar, not started yet.</summary>
    public static YjsSidecar Create(Action<SidecarOptions>? configure = null, ILogger<YjsSidecar>? logger = null)
    {
        var options = new SidecarOptions { ScriptPath = SidecarFiles.ScriptPath };
        configure?.Invoke(options);
        return new YjsSidecar(options, AppContext.BaseDirectory, TimeProvider.System, logger ?? NullLogger<YjsSidecar>.Instance);
    }

    /// <summary>A stand-in that misbehaves on purpose (Fixtures/sidecars), not started yet.</summary>
    public static YjsSidecar CreateStub(string script, Action<SidecarOptions>? configure = null, ILogger<YjsSidecar>? logger = null) =>
        Create(options =>
        {
            options.ScriptPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sidecars", script);
            configure?.Invoke(options);
        }, logger);

    /// <summary>Starts the host part and waits until the process answers its start-up check.</summary>
    public static async Task<YjsSidecar> StartAsync(YjsSidecar sidecar)
    {
        await sidecar.StartAsync(CancellationToken.None);
        await sidecar.WaitUntilReadyAsync(new CancellationTokenSource(StartTimeout).Token);
        return sidecar;
    }

    public static async Task StopAsync(YjsSidecar sidecar)
    {
        await sidecar.StopAsync(CancellationToken.None);
        sidecar.Dispose();
    }
}
