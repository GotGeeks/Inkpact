using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Spike.Crdt;

namespace Spike.Server.Tests;

/// <summary>One test per guarantee in architecture → "Compaction guarantees", on real Yjs updates and Postgres.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CompactorTests(PostgresFixture fixture) : IDisposable
{
    private readonly YjsSidecarCodec _codec = TestSidecar.Create();
    private readonly YjsSidecarCodec _oracle = TestSidecar.Create();
    private readonly IReadOnlyList<byte[]> _stream = UpdateStream.Get(300, 21);

    public void Dispose()
    {
        _codec.Dispose();
        _oracle.Dispose();
    }

    private Compactor NewCompactor(ICrdtCodec? codec = null) =>
        new(fixture.DataSource, codec ?? _codec, NullLogger<Compactor>.Instance);

    private async Task AppendAsync(string pageId, int from, int count)
    {
        for (var i = from; i < from + count; i++)
        {
            await fixture.Store.AppendAsync(pageId, _stream[i], CancellationToken.None);
        }
    }

    /// <summary>The never-compacted document made of the first <paramref name="count"/> updates.</summary>
    private byte[] Oracle(int count) => _oracle.Merge(_stream.Take(count));

    private async Task AssertPageEqualsOracleAsync(string pageId, int count)
    {
        var load = await fixture.Store.LoadPageAsync(pageId, CancellationToken.None);
        Assert.Equal(count, load.AppliedUpdates);
        DocumentAssert.Same(_codec, Oracle(count), _codec.Merge(load.All), $"page {pageId} after {count} updates");
    }

    [Fact]
    public async Task Compaction_folds_updates_into_a_snapshot_and_archives_them()
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 100);

        var result = await NewCompactor().CompactAsync(page);

        Assert.Equal(CompactionResult.Compacted, result);
        var stats = await fixture.Store.GetStatsAsync(page, CancellationToken.None);
        Assert.Equal((0, 100, 100, 1), (stats.LiveUpdates, stats.ArchivedUpdates, stats.SnapshotUpdateCount, stats.SnapshotVersion));
        await AssertPageEqualsOracleAsync(page, 100);
    }

    [Theory]
    [InlineData(CompactionStage.Locked)]
    [InlineData(CompactionStage.Read)]
    [InlineData(CompactionStage.Merged)]
    [InlineData(CompactionStage.Verified)]
    [InlineData(CompactionStage.SnapshotWritten)]
    [InlineData(CompactionStage.Archived)]
    [InlineData(CompactionStage.Deleted)]
    public async Task Transactional_a_crash_at_any_stage_leaves_the_old_state_and_compaction_still_works(CompactionStage crashAt)
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 50);
        await NewCompactor().CompactAsync(page); // an existing snapshot, so overwriting it is also at risk
        await AppendAsync(page, 50, 50);
        var before = await Capture(page);

        var crashing = NewCompactor();
        crashing.StageHook = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrash>(() => crashing.CompactAsync(page));

        Assert.Equal(before, await Capture(page));

        // The next run starts from the old state and gets it right.
        Assert.Equal(CompactionResult.Compacted, await NewCompactor().CompactAsync(page));
        await AssertPageEqualsOracleAsync(page, 100);
    }

    [Fact]
    public async Task Watermark_updates_that_arrive_during_compaction_stay_live_and_are_folded_in_next()
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 50);
        var merged = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var compactor = NewCompactor();
        compactor.StageHook = async stage =>
        {
            if (stage == CompactionStage.Merged)
            {
                merged.SetResult();
                await release.Task;
            }
        };

        var compacting = compactor.CompactAsync(page);
        await merged.Task;
        await AppendAsync(page, 50, 20); // appends must not wait for the compaction either
        release.SetResult();

        Assert.Equal(CompactionResult.Compacted, await compacting);
        var stats = await fixture.Store.GetStatsAsync(page, CancellationToken.None);
        Assert.Equal((20, 50), (stats.LiveUpdates, stats.ArchivedUpdates));
        await AssertPageEqualsOracleAsync(page, 70);

        Assert.Equal(CompactionResult.Compacted, await NewCompactor().CompactAsync(page));
        await AssertPageEqualsOracleAsync(page, 70);
        Assert.Equal(70, (await fixture.Store.GetStatsAsync(page, CancellationToken.None)).ArchivedUpdates);
    }

    [Fact]
    public async Task One_at_a_time_a_second_compaction_of_the_same_page_is_skipped()
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 50);
        var merged = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = NewCompactor();
        first.StageHook = async stage =>
        {
            if (stage == CompactionStage.Merged)
            {
                merged.SetResult();
                await release.Task;
            }
        };

        var running = first.CompactAsync(page);
        await merged.Task;
        var second = await NewCompactor().CompactAsync(page);
        release.SetResult();

        Assert.Equal(CompactionResult.Skipped, second);
        Assert.Equal(CompactionResult.Compacted, await running);
        // Archived exactly once: a double archive would have failed on the primary key.
        Assert.Equal(50, (await fixture.Store.GetStatsAsync(page, CancellationToken.None)).ArchivedUpdates);
    }

    [Fact]
    public async Task One_at_a_time_other_pages_are_not_blocked()
    {
        var busy = PostgresFixture.NewPageId();
        var other = PostgresFixture.NewPageId();
        await AppendAsync(busy, 0, 20);
        await AppendAsync(other, 0, 20);
        var merged = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = NewCompactor();
        first.StageHook = async stage =>
        {
            if (stage == CompactionStage.Merged)
            {
                merged.SetResult();
                await release.Task;
            }
        };

        var running = first.CompactAsync(busy);
        await merged.Task;
        var result = await NewCompactor().CompactAsync(other);
        release.SetResult();
        await running;

        Assert.Equal(CompactionResult.Compacted, result);
    }

    [Fact]
    public async Task Idempotent_compacting_twice_equals_compacting_once()
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 100);
        await NewCompactor().CompactAsync(page);
        var afterFirst = await Capture(page);

        var second = await NewCompactor().CompactAsync(page);

        Assert.Equal(CompactionResult.NothingToDo, second);
        Assert.Equal(afterFirst, await Capture(page));
    }

    [Fact]
    public void Yjs_in_one_transaction_can_lose_a_deletion_so_merge_applies_updates_one_at_a_time()
    {
        // Found by the 50,000-edit stress test (seed 43): a snapshot plus the updates after it, applied in one
        // Yjs transaction, ends without one deletion that every browser has. Invisible in the text; only a
        // comparison of delete sets sees it. Minimised by scripts/yjs-one-transaction-repro.ts.
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "yjs-one-transaction-repro.json")));
        var snapshot = Convert.FromBase64String(json.RootElement.GetProperty("snapshot").GetString()!);
        var updates = json.RootElement.GetProperty("updates").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString()!));
        byte[][] inputs = [snapshot, .. updates];

        var merged = _codec.Fingerprint(_codec.Merge(inputs));

        Assert.Equal(merged, _codec.Fingerprint(_codec.Replay(inputs)));
        Assert.NotEqual(merged, _codec.Fingerprint(_codec.MergeInOneTransaction(inputs)));
    }

    [Fact]
    public async Task Pages_that_are_not_tiptap_documents_compact_too()
    {
        // Y.Text "t" edited three times (insert, insert, delete): no 'default' XML fragment, so ProseMirror
        // text extraction has nothing valid to read. Found by running the real worker on such a page.
        string[] hex = ["0101922100040101740361626300", "0101922103849221020364656600", "00019221010102"];
        var page = PostgresFixture.NewPageId();
        foreach (var update in hex)
        {
            await fixture.Store.AppendAsync(page, Convert.FromHexString(update), CancellationToken.None);
        }

        Assert.Equal(CompactionResult.Compacted, await NewCompactor().CompactAsync(page));

        var load = await fixture.Store.LoadPageAsync(page, CancellationToken.None);
        DocumentAssert.Same(_codec, _codec.Merge(hex.Select(Convert.FromHexString)), _codec.Merge(load.All), "plain text page");
        Assert.Equal(3, load.SnapshotUpdateCount);
    }

    [Fact]
    public async Task Verified_a_merge_that_differs_from_the_replay_is_not_committed()
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 50);
        var before = await Capture(page);

        var error = await Assert.ThrowsAsync<CompactionVerificationException>(
            () => NewCompactor(new DropsTheLastUpdate(_codec)).CompactAsync(page));

        Assert.Contains("replay of snapshot and updates", error.Message);
        Assert.Equal(before, await Capture(page));
    }

    [Fact]
    public async Task Verified_a_wrong_earlier_snapshot_is_caught_by_the_full_history_replay()
    {
        var page = PostgresFixture.NewPageId();
        await AppendAsync(page, 0, 50);
        await NewCompactor().CompactAsync(page);
        await AppendAsync(page, 50, 30);

        // Corrupt the stored snapshot to a valid but older document: it is consistent with itself, so only
        // a replay that ignores the snapshot can notice.
        await using (var command = fixture.DataSource.CreateCommand("update page_snapshots set state = $1 where page_id = $2"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = Oracle(10) });
            command.Parameters.Add(new NpgsqlParameter { Value = page });
            await command.ExecuteNonQueryAsync();
        }

        var before = await Capture(page);

        var error = await Assert.ThrowsAsync<CompactionVerificationException>(() => NewCompactor().CompactAsync(page));

        Assert.Contains("full history", error.Message);
        Assert.Equal(before, await Capture(page));
    }

    [Fact]
    public async Task Subscribers_loading_while_compaction_runs_never_miss_an_update()
    {
        const int total = 240;
        var page = PostgresFixture.NewPageId();
        using var readerCodec = TestSidecar.Create();
        var writing = true;

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < total; i++)
            {
                await fixture.Store.AppendAsync(page, _stream[i], CancellationToken.None);
                if (i % 7 == 0)
                {
                    await Task.Delay(1);
                }
            }

            writing = false;
        });
        var compacting = Task.Run(async () =>
        {
            var compactor = NewCompactor();
            while (writing)
            {
                await compactor.CompactAsync(page);
                await Task.Delay(2);
            }
        });

        // Every consistent view must be exactly the first AppliedUpdates updates: nothing missing, nothing twice.
        var views = 0;
        var viewsWithSnapshot = 0;
        while (writing)
        {
            var load = await fixture.Store.LoadPageAsync(page, CancellationToken.None);
            if (load.AppliedUpdates == 0)
            {
                continue;
            }

            DocumentAssert.Same(readerCodec, Oracle((int)load.AppliedUpdates), readerCodec.Merge(load.All), $"view of {load.AppliedUpdates} updates");
            views++;
            viewsWithSnapshot += load.Snapshot is null ? 0 : 1;
        }

        await Task.WhenAll(writer, compacting);
        await NewCompactor().CompactAsync(page);
        await AssertPageEqualsOracleAsync(page, total);
        Assert.True(viewsWithSnapshot > 0, $"no view saw a snapshot ({views} views): the test did not exercise compaction");
    }

    /// <summary>Everything a crashed or skipped compaction must leave untouched.</summary>
    private async Task<(string Snapshot, PageStats Stats, string Updates)> Capture(string pageId)
    {
        var load = await fixture.Store.LoadPageAsync(pageId, CancellationToken.None);
        var stats = await fixture.Store.GetStatsAsync(pageId, CancellationToken.None);
        static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
        return (load.Snapshot is null ? "none" : Hash(load.Snapshot), stats, string.Join(',', load.Updates.Select(Hash)));
    }

    /// <summary>A codec whose Merge loses the last input, so the merge no longer matches a replay.</summary>
    private sealed class DropsTheLastUpdate(ICrdtCodec inner) : ICrdtCodec
    {
        public byte[] Merge(IEnumerable<byte[]> updates)
        {
            var all = updates.ToList();
            return inner.Merge(all.Take(all.Count - 1));
        }

        public byte[] Replay(IEnumerable<byte[]> updates) => inner.Replay(updates);

        public byte[] Diff(byte[] state, byte[] stateVector) => inner.Diff(state, stateVector);

        public byte[] Fingerprint(byte[] state) => inner.Fingerprint(state);

        public byte[] StateVector(byte[] state) => inner.StateVector(state);

        public string ExtractText(byte[] state) => inner.ExtractText(state);
    }
}
