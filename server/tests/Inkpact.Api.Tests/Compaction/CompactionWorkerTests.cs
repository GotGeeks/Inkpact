using System.Diagnostics;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Inkpact.Api.Tests.Compaction;

/// <summary>
/// When the worker compacts which page, what it does with pages that fail, and the purge of the archive. The tests call
/// the worker's round and the purge directly with a clock that only moves when they say so; the last one runs the
/// worker as the app does.
/// </summary>
public class CompactionWorkerTests(PostgresFixture postgres, YjsSidecarFixture yjs) : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    private static IReadOnlyList<byte[]> Stream(int edits = 60, int seed = 1) => EditStreams.Get(edits, seed);

    [Fact]
    public void The_defaults_are_the_ones_of_the_architecture()
    {
        var options = new CompactionOptions();

        options.Enabled.ShouldBeTrue();
        options.UpdateThreshold.ShouldBe(500);
        options.IdleAfter.ShouldBe(TimeSpan.FromMinutes(10));
        options.ArchiveRetention.ShouldBe(TimeSpan.FromDays(30));
        options.PollInterval.ShouldBe(TimeSpan.FromSeconds(30));
        options.PagesPerRound.ShouldBe(100);
        options.MaxUpdatesPerCompaction.ShouldBe(5_000);
        options.FullHistoryEvery.ShouldBe(10);
        options.FailureBackoff.ShouldBe(TimeSpan.FromHours(1));
        options.PurgeInterval.ShouldBe(TimeSpan.FromHours(1));
        options.PurgeBatchSize.ShouldBe(10_000);
    }

    // ---- which pages ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_with_the_threshold_of_live_updates_is_compacted_and_one_below_it_waits_until_it_is_idle()
    {
        var stream = Stream(560, 51);
        stream.Count.ShouldBeGreaterThanOrEqualTo(500);
        var full = await AddPageAsync();
        var below = await AddPageAsync();
        await AppendBulkAsync(full, stream.Take(500));
        await AppendBulkAsync(below, stream.Take(499));
        var worker = CreateWorker();

        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(1, 0, 0, 0, false));
        (await ReadStoredAsync(full)).Snapshot.ShouldNotBeNull();
        (await ReadStoredAsync(below)).Snapshot.ShouldBeNull();

        // Not yet idle, a moment short of it.
        Clock.Advance(Options.IdleAfter - TimeSpan.FromSeconds(1));
        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(0, 0, 0, 0, false));

        Clock.Advance(TimeSpan.FromSeconds(2));
        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(1, 0, 0, 0, false));
        (await ReadStoredAsync(below)).Snapshot!.UpdateCount.ShouldBe(499);
    }

    [Fact]
    public async Task A_compacted_page_is_not_looked_at_again_until_it_gets_new_updates()
    {
        var page = await AddPageAsync();
        await AppendBulkAsync(page, Stream().Take(5));
        Clock.Advance(Options.IdleAfter + TimeSpan.FromSeconds(1));
        var worker = CreateWorker();
        (await worker.RunRoundAsync(default)).Compacted.ShouldBe(1);

        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(0, 0, 0, 0, false));
    }

    [Fact]
    public async Task The_page_that_has_waited_longest_goes_first_and_a_round_is_limited_to_its_page_count()
    {
        Options.UpdateThreshold = 1;
        Options.PagesPerRound = 2;
        var newest = await AddPageAsync();
        var oldest = await AddPageAsync();
        var middle = await AddPageAsync();
        await AppendBulkAsync(newest, Stream().Take(3), Start.AddMinutes(3));
        await AppendBulkAsync(oldest, Stream().Take(3), Start.AddMinutes(1));
        await AppendBulkAsync(middle, Stream().Take(3), Start.AddMinutes(2));
        var worker = CreateWorker();

        (await worker.RunRoundAsync(default)).Compacted.ShouldBe(2);

        (await ReadStoredAsync(oldest)).Snapshot.ShouldNotBeNull();
        (await ReadStoredAsync(middle)).Snapshot.ShouldNotBeNull();
        (await ReadStoredAsync(newest)).Snapshot.ShouldBeNull();
        (await worker.RunRoundAsync(default)).Compacted.ShouldBe(1);
        (await ReadStoredAsync(newest)).Snapshot.ShouldNotBeNull();
    }

    // ---- pages that fail -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_that_fails_is_left_alone_for_longer_each_time_and_the_other_pages_are_still_compacted()
    {
        Options.UpdateThreshold = 1;
        var broken = await AddPageAsync();
        var fine = await AddPageAsync();
        await AppendBulkAsync(broken, [[0xFF, 0xFF, 0xFF, 0xFF]], Start.AddMinutes(-1));
        await AppendBulkAsync(fine, Stream().Take(10));
        var worker = CreateWorker();

        // The broken page is first in line, and does not stop the round.
        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(1, 0, 0, 1, false));
        (await ReadStoredAsync(fine)).Snapshot.ShouldNotBeNull();

        // Within the first hour it is not tried again, however new updates arrive.
        await AppendBulkAsync(fine, Stream().Skip(10).Take(10));
        Clock.Advance(TimeSpan.FromMinutes(59));
        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(1, 0, 0, 0, false));

        // After the hour it is, and fails again; now the wait is two hours.
        Clock.Advance(TimeSpan.FromMinutes(2));
        (await worker.RunRoundAsync(default)).Failed.ShouldBe(1);
        Clock.Advance(TimeSpan.FromMinutes(119));
        (await worker.RunRoundAsync(default)).Failed.ShouldBe(0);
        Clock.Advance(TimeSpan.FromMinutes(2));
        (await worker.RunRoundAsync(default)).Failed.ShouldBe(1);
    }

    [Fact]
    public async Task A_page_that_is_repaired_is_compacted_after_its_wait_and_starts_over()
    {
        Options.UpdateThreshold = 1;
        var page = await AddPageAsync();
        await AppendBulkAsync(page, Stream().Take(5));
        var failing = true;
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(default!, default).ReturnsForAnyArgs(call =>
            failing ? Task.FromException<YjsCompaction>(new YjsRejectedException("bad")) : Codec.CompactAsync(call.ArgAt<IReadOnlyList<byte[]>>(0), call.ArgAt<CancellationToken>(1)));
        var worker = CreateWorker(codec);

        (await worker.RunRoundAsync(default)).Failed.ShouldBe(1);
        failing = false;
        Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        (await worker.RunRoundAsync(default)).Compacted.ShouldBe(1);
    }

    [Fact]
    public async Task A_sidecar_that_is_down_ends_the_round_without_an_error_and_the_page_waits()
    {
        Options.UpdateThreshold = 1;
        var first = await AddPageAsync();
        var second = await AddPageAsync();
        await AppendBulkAsync(first, Stream().Take(5), Start.AddMinutes(1));
        await AppendBulkAsync(second, Stream().Take(5), Start.AddMinutes(2));
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(default!, default).ThrowsAsyncForAnyArgs(new YjsUnavailableException("restarting"));
        var worker = CreateWorker(codec);

        // One attempt, then the round ends: the second page was not even tried.
        (await worker.RunRoundAsync(default)).ShouldBe(new CompactionRound(0, 0, 0, 0, true));
        await codec.ReceivedWithAnyArgs(1).CompactAsync(default!, default);

        // The next round tries the other page, not the one that just took the sidecar down.
        (await worker.RunRoundAsync(default)).SidecarUnavailable.ShouldBeTrue();
        await codec.ReceivedWithAnyArgs(2).CompactAsync(default!, default);
        (await worker.RunRoundAsync(default)).SidecarUnavailable.ShouldBeFalse();
        await codec.ReceivedWithAnyArgs(2).CompactAsync(default!, default);
    }

    [Fact]
    public async Task A_worker_that_is_off_does_nothing()
    {
        Options.Enabled = false;
        var worker = CreateWorker();

        await worker.StartAsync(default);

        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
    }

    // ---- the purge -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_purge_deletes_only_what_is_past_the_retention_and_stops_the_history_check_of_those_pages()
    {
        var stream = Stream(90, 2);
        var old = await AddPageAsync();
        var mixed = await AddPageAsync();
        var recent = await AddPageAsync();
        await AppendBulkAsync(old, stream.Take(10));
        await AppendBulkAsync(mixed, stream.Take(10));
        var compactor = CreateCompactor();
        (await compactor.CompactAsync(old, default)).ShouldBe(CompactionResult.Compacted);
        (await compactor.CompactAsync(mixed, default)).ShouldBe(CompactionResult.Compacted);

        Clock.Advance(TimeSpan.FromDays(20));
        await AppendBulkAsync(mixed, stream.Skip(10).Take(10), Clock.GetUtcNow());
        await AppendBulkAsync(recent, stream.Take(10), Clock.GetUtcNow());
        (await compactor.CompactAsync(mixed, default)).ShouldBe(CompactionResult.Compacted);
        (await compactor.CompactAsync(recent, default)).ShouldBe(CompactionResult.Compacted);

        // 35 days after the first compactions and 15 after the second.
        Clock.Advance(TimeSpan.FromDays(15));
        var purged = await CreatePurge().PurgeAsync(default);

        purged.ShouldBe(20);
        var stored = new Dictionary<string, StoredPage>
        {
            ["old"] = await ReadStoredAsync(old),
            ["mixed"] = await ReadStoredAsync(mixed),
            ["recent"] = await ReadStoredAsync(recent),
        };
        stored["old"].ArchivedSeqs.ShouldBeEmpty();
        stored["old"].Snapshot!.HistoryComplete.ShouldBeFalse();
        stored["mixed"].ArchivedSeqs.Length.ShouldBe(10);
        stored["mixed"].Snapshot!.HistoryComplete.ShouldBeFalse();
        stored["recent"].ArchivedSeqs.Length.ShouldBe(10);
        stored["recent"].Snapshot!.HistoryComplete.ShouldBeTrue();

        // The pages themselves are untouched: the snapshot is still the page.
        (await StoredFingerprintAsync(old)).ShouldBe(await Codec.FingerprintAsync(stream.Take(10).ToList(), default));
        (await CreatePurge().PurgeAsync(default)).ShouldBe(0);
    }

    [Fact]
    public async Task A_long_backlog_is_purged_in_batches()
    {
        Options.PurgeBatchSize = 7;
        var page = await AddPageAsync();
        await AppendBulkAsync(page, Stream(90, 3).Take(30));
        (await CreateCompactor().CompactAsync(page, default)).ShouldBe(CompactionResult.Compacted);
        Clock.Advance(Options.ArchiveRetention + TimeSpan.FromSeconds(1));

        (await CreatePurge().PurgeAsync(default)).ShouldBe(30);

        (await ReadStoredAsync(page)).ArchivedSeqs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_purge_waits_for_a_compaction_that_is_running_on_the_page()
    {
        // The compaction holds the snapshot row from its read to its commit; the purge needs that row, so it cannot
        // shorten the archive under a full-history check that has already seen history_complete = true.
        var stream = Stream(90, 4);
        var page = await AddPageAsync();
        await AppendBulkAsync(page, stream.Take(10));
        (await CreateCompactor().CompactAsync(page, default)).ShouldBe(CompactionResult.Compacted);
        await AppendBulkAsync(page, stream.Skip(10).Take(10));
        Clock.Advance(Options.ArchiveRetention + TimeSpan.FromSeconds(1));

        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Hooks.OnStage = async stage =>
        {
            if (stage == CompactionStage.Read)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };
        var compacting = CreateCompactor().CompactAsync(page, default);
        await reached.Task;
        var purging = CreatePurge().PurgeAsync(default);

        var finishedEarly = await Task.WhenAny(purging, Task.Delay(500)) == purging;
        release.SetResult();

        finishedEarly.ShouldBeFalse("the purge must wait for the compaction");
        (await compacting).ShouldBe(CompactionResult.Compacted);
        (await purging).ShouldBe(10);
        (await ReadStoredAsync(page)).Snapshot!.HistoryComplete.ShouldBeFalse();
    }

    // ---- as the app runs it ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hosted_worker_compacts_a_page_without_anyone_asking()
    {
        var settings = new Dictionary<string, string>
        {
            ["Compaction:Enabled"] = "True",
            ["Compaction:PollInterval"] = "00:00:00.200",
            ["Compaction:UpdateThreshold"] = "5",
        };
        await using var factory = new InkpactFactory(ConnectionString, sidecar: true, settings: settings);
        var page = await AddPageAsync();
        await AppendBulkAsync(page, Stream().Take(8));

        var clock = Stopwatch.StartNew();
        while ((await ReadStoredAsync(page)).Snapshot is null)
        {
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "gave up waiting for the worker");
            _ = factory.Services; // starts the app and with it the worker and the sidecar
            await Task.Delay(100);
        }

        (await ReadStoredAsync(page)).LiveSeqs.ShouldBeEmpty();
    }
}
