using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Spike.Crdt;
using Xunit.Abstractions;

namespace Spike.Server.Tests;

/// <summary>
/// Phase 0 gate: random edits, appended while compaction runs repeatedly (with crashes and parallel
/// attempts mixed in), must always end in the document an uncompacted replay gives, and storage
/// must not grow without bound.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CompactionStressTests(PostgresFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public Task Five_thousand_edits() => RunAsync(edits: 5_000, seed: 31, checkEvery: 1, minGap: 30, maxGap: 150);

    // About 50,000 updates each; slow, so they run only with INKPACT_STRESS=1.
    [StressFact]
    public Task Fifty_thousand_edits_seed_41() => RunAsync(edits: 50_500, seed: 41, checkEvery: 3, minGap: 100, maxGap: 500);

    [StressFact]
    public Task Fifty_thousand_edits_seed_42() => RunAsync(edits: 50_500, seed: 42, checkEvery: 3, minGap: 100, maxGap: 500);

    [StressFact]
    public Task Fifty_thousand_edits_seed_43() => RunAsync(edits: 50_500, seed: 43, checkEvery: 3, minGap: 100, maxGap: 500);

    private async Task RunAsync(int edits, int seed, int checkEvery, int minGap, int maxGap)
    {
        var stream = UpdateStream.Get(edits, seed);
        var pageId = PostgresFixture.NewPageId();
        var random = new Random(seed);
        using var compactionCodec = TestSidecar.Create();
        using var oracle = TestSidecar.Create(); // never sees the database, only the raw stream
        var compactor = new Compactor(fixture.DataSource, compactionCodec, NullLogger<Compactor>.Instance);
        var rival = new Compactor(fixture.DataSource, compactionCodec, NullLogger<Compactor>.Instance);
        var clock = Stopwatch.StartNew();

        var appended = 0;
        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < stream.Count; i++)
            {
                await fixture.Store.AppendAsync(pageId, stream[i], CancellationToken.None);
                Volatile.Write(ref appended, i + 1);
                if (i % 40 == 0)
                {
                    await Task.Yield();
                }
            }
        });

        var counts = new Dictionary<string, int> { ["compacted"] = 0, ["crashed"] = 0, ["parallel"] = 0, ["skipped"] = 0, ["checked"] = 0 };
        var history = new List<(int Appended, long SnapshotBytes, long LiveBytes)>();
        var nextAt = random.Next(minGap, maxGap);

        while (!writer.IsCompleted)
        {
            if (Volatile.Read(ref appended) < nextAt)
            {
                await Task.Delay(1);
                continue;
            }

            nextAt = Volatile.Read(ref appended) + random.Next(minGap, maxGap);
            var mode = random.Next(10);
            var compacted = false;

            if (mode < 2)
            {
                // The process dies at a random stage: nothing may change.
                var crashAt = (CompactionStage)random.Next(Enum.GetValues<CompactionStage>().Length);
                var before = await ArchiveState(pageId);
                compactor.StageHook = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;
                await Assert.ThrowsAsync<SimulatedCrash>(() => compactor.CompactAsync(pageId));
                compactor.StageHook = null;
                Assert.Equal(before, await ArchiveState(pageId));
                counts["crashed"]++;
            }
            else if (mode == 2)
            {
                // Two compactions of the same page at once: whichever way it falls, state stays consistent.
                var results = await Task.WhenAll(compactor.CompactAsync(pageId), rival.CompactAsync(pageId));
                counts["parallel"]++;
                counts["skipped"] += results.Count(r => r == CompactionResult.Skipped);
                compacted = results.Any(r => r == CompactionResult.Compacted);
            }
            else
            {
                Assert.Equal(CompactionResult.Compacted, await compactor.CompactAsync(pageId));
                compacted = true;
            }

            if (compacted)
            {
                counts["compacted"]++;
                if (counts["compacted"] % checkEvery == 0)
                {
                    await CheckAsync(pageId, stream, compactionCodec, oracle, history);
                    counts["checked"]++;
                }
            }
        }

        await writer;

        // The writer is done: one last compaction leaves nothing live, and the page equals the full replay.
        // It may find nothing to do when the loop's last compaction already folded in the final update.
        Assert.NotEqual(CompactionResult.Skipped, await compactor.CompactAsync(pageId));
        Assert.Equal(CompactionResult.NothingToDo, await compactor.CompactAsync(pageId));
        await CheckAsync(pageId, stream, compactionCodec, oracle, history);

        var stats = await fixture.Store.GetStatsAsync(pageId, CancellationToken.None);
        var freshState = oracle.Merge(stream);
        var logBytes = stream.Sum(u => (long)u.Length);
        Assert.Equal(0, stats.LiveUpdates);
        Assert.Equal(stream.Count, stats.ArchivedUpdates);
        Assert.Equal(stream.Count, stats.SnapshotUpdateCount);
        Assert.True(stats.SnapshotBytes <= 2 * freshState.Length, $"snapshot {stats.SnapshotBytes} bytes exceeds 2x a fresh state ({freshState.Length})");

        var peakLive = history.Max(h => h.LiveBytes);
        output.WriteLine(
            $"{stream.Count} updates ({logBytes} bytes of log) in {clock.Elapsed.TotalSeconds:F0} s; " +
            $"compactions {counts["compacted"]} (checked {counts["checked"]}), crash-injected {counts["crashed"]}, " +
            $"parallel attempts {counts["parallel"]} (skipped {counts["skipped"]}), {stats.SnapshotVersion} snapshot versions");
        output.WriteLine(
            $"final snapshot {stats.SnapshotBytes} bytes vs {freshState.Length} bytes for a fresh encode of the same document; " +
            $"log {logBytes} bytes; peak live updates between compactions {peakLive} bytes; archive {stats.ArchivedUpdates} rows");
    }

    /// <summary>
    /// The state an aborted compaction must leave alone. Live updates are left out: the writer keeps adding to them.
    /// </summary>
    private async Task<(long SnapshotBytes, long SnapshotUpdateCount, int Version, long Archived, string SnapshotHash)> ArchiveState(string pageId)
    {
        var load = await fixture.Store.LoadPageAsync(pageId, CancellationToken.None);
        var stats = await fixture.Store.GetStatsAsync(pageId, CancellationToken.None);
        var hash = load.Snapshot is null ? "none" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(load.Snapshot))[..12];
        return (stats.SnapshotBytes, stats.SnapshotUpdateCount, stats.SnapshotVersion, stats.ArchivedUpdates, hash);
    }

    private async Task CheckAsync(
        string pageId,
        IReadOnlyList<byte[]> stream,
        ICrdtCodec codec,
        ICrdtCodec oracle,
        List<(int Appended, long SnapshotBytes, long LiveBytes)> history)
    {
        // One consistent view of the page, exactly as a subscriber would load it.
        var load = await fixture.Store.LoadPageAsync(pageId, CancellationToken.None);
        var applied = (int)load.AppliedUpdates;

        // The whole page equals the uncompacted replay of the first `applied` updates...
        DocumentAssert.Same(oracle, oracle.Merge(stream.Take(applied)), codec.Merge(load.All), $"page after {applied} updates");

        // ...and so does the snapshot on its own, which is what compaction produced.
        Assert.NotNull(load.Snapshot);
        var folded = (int)load.SnapshotUpdateCount;
        var expectedSnapshot = oracle.Merge(stream.Take(folded));
        DocumentAssert.Same(oracle, expectedSnapshot, load.Snapshot, $"snapshot of {folded} updates");
        Assert.True(
            load.Snapshot.Length <= 2 * expectedSnapshot.Length,
            $"snapshot of {folded} updates is {load.Snapshot.Length} bytes, more than 2x a fresh encode ({expectedSnapshot.Length})");

        // Nothing lost, nothing twice: every folded update is in the archive exactly once.
        var stats = await fixture.Store.GetStatsAsync(pageId, CancellationToken.None);
        Assert.Equal(stats.SnapshotUpdateCount, stats.ArchivedUpdates);

        history.Add((applied, stats.SnapshotBytes, stats.LiveUpdateBytes));
    }
}
