namespace Inkpact.Api.Crdt;

/// <summary>The Yjs sidecar, from the <c>Sidecar</c> configuration section.</summary>
public sealed class SidecarOptions
{
    public const string SectionName = "Sidecar";

    /// <summary>
    /// False behaves exactly like a sidecar that cannot start: no process, and every call fails with
    /// <see cref="YjsUnavailableException"/>. For tests that have no use for Yjs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Node executable. Empty looks for <c>node</c> on the PATH.</summary>
    public string NodePath { get; set; } = "";

    /// <summary>The sidecar's entry point, relative to the content root (or absolute). The default is where a published app has it.</summary>
    public string ScriptPath { get; set; } = "sidecar/src/main.ts";

    /// <summary>The sidecar's V8 heap limit. A page it cannot replay within this fails that call and restarts the process.</summary>
    public int MaxHeapMb { get; set; } = 512;

    /// <summary>
    /// How long a subscribe waits for a diff. A sidecar that takes longer is killed, since it handles one request at a
    /// time and a stuck one blocks all the others; the subscriber gets the stored log instead.
    /// </summary>
    public TimeSpan DiffTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long compaction, verification and the start-up check may take.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
