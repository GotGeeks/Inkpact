namespace Inkpact.Api.Compaction;

/// <summary>The points in a compaction where a test can inject a crash, or block to let appends in.</summary>
public enum CompactionStage
{
    /// <summary>The transaction is open and the lock not yet asked for: where a second compaction can be held while a first one commits.</summary>
    Begun,
    Locked,
    Read,
    Merged,
    Verified,
    SnapshotWritten,
    Archived,
    Deleted,
    TextWritten,
    LinksWritten,
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
