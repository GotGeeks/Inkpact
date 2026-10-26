namespace Inkpact.Api.Compaction;

/// <summary>The <c>Compaction:</c> section of the configuration.</summary>
public sealed class CompactionOptions
{
    public const string SectionName = "Compaction";

    /// <summary>Whether the background worker runs. The compactor itself can always be called.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker looks for pages to compact.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A page with this many live updates is compacted.</summary>
    public int UpdateThreshold { get; set; } = 500;

    /// <summary>A page whose newest live update is older than this is compacted, however few updates it has.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The most pages one round compacts; the rest wait for the next round.</summary>
    public int PagesPerRound { get; set; } = 100;

    /// <summary>The most updates one compaction folds in. A longer log takes several compactions.</summary>
    public int MaxUpdatesPerCompaction { get; set; } = 5_000;

    /// <summary>Every this many versions of a page's snapshot, it is also checked against a replay of its whole history.</summary>
    public int FullHistoryEvery { get; set; } = 10;

    /// <summary>How long a page that failed (or took the sidecar down) is left alone. It doubles each time, up to a day.</summary>
    public TimeSpan FailureBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long compacted updates are kept in the archive.</summary>
    public TimeSpan ArchiveRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the worker purges the archive.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The most archived updates one purge transaction deletes, so a long backlog never holds one for long.</summary>
    public int PurgeBatchSize { get; set; } = 10_000;
}
