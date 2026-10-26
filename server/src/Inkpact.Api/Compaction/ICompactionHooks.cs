namespace Inkpact.Api.Compaction;

/// <summary>The points in a compaction where a test can inject a crash, or block to let appends in.</summary>
public enum CompactionStage
{
    Locked,
    Read,
    Merged,
    Verified,
    SnapshotWritten,
    Archived,
    Deleted,
    TextWritten,
}

/// <summary>
/// Called after each stage of a compaction, inside its transaction. The app's is a no-op; tests throw to simulate a
/// crash, or wait to let updates in.
/// </summary>
public interface ICompactionHooks
{
    Task AfterStageAsync(Guid pageId, CompactionStage stage, CancellationToken cancellationToken);
}

internal sealed class NoCompactionHooks : ICompactionHooks
{
    public Task AfterStageAsync(Guid pageId, CompactionStage stage, CancellationToken cancellationToken) => Task.CompletedTask;
}
