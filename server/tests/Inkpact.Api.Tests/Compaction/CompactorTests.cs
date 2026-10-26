using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Inkpact.Api.Tests.Compaction;

/// <summary>
/// Every guarantee in the architecture's "Compaction guarantees", one at a time, against the real Postgres and the real
/// Yjs sidecar (scenario 12 of the spike): the page is always its snapshot plus its live updates, and a compaction
/// never changes that, whatever happens to it.
/// </summary>
public class CompactorTests(PostgresFixture postgres, YjsSidecarFixture yjs) : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    public static IEnumerable<object[]> Stages() =>
        Enum.GetValues<CompactionStage>().SelectMany(stage => new[] { new object[] { stage, false }, new object[] { stage, true } });

    private static IReadOnlyList<byte[]> Stream(int edits = 80, int seed = 1) => EditStreams.Get(edits, seed);

    private async Task<Guid> PageWithAsync(IEnumerable<byte[]> updates)
    {
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, updates);
        return pageId;
    }

    [Fact]
    public async Task Compacting_folds_the_live_updates_into_a_snapshot_and_archives_them()
    {
        var stream = Stream();
        var pageId = await PageWithAsync(stream);
        var oracle = await Codec.FingerprintAsync(stream, default);

        var result = await CreateCompactor().CompactAsync(pageId, default);

        result.ShouldBe(CompactionResult.Compacted);
        var stored = await ReadStoredAsync(pageId);
        stored.LiveSeqs.ShouldBeEmpty();
        stored.ArchivedSeqs.Length.ShouldBe(stream.Count);
        stored.Snapshot.ShouldNotBeNull();
        stored.Snapshot.Version.ShouldBe(1);
        stored.Snapshot.UpdateCount.ShouldBe(stream.Count);
        stored.Snapshot.Watermark.ShouldBe(stored.ArchivedSeqs[^1]);
        stored.Snapshot.HistoryComplete.ShouldBeTrue();
        stored.Snapshot.UpdatedAt.ShouldBe(Start);
        (await StoredFingerprintAsync(pageId)).ShouldBe(oracle);
    }

    [Fact]
    public async Task A_page_without_updates_has_nothing_to_do()
    {
        var pageId = await AddPageAsync();

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.NothingToDo);

        (await ReadStoredAsync(pageId)).Snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task Compacting_twice_changes_nothing_the_second_time()
    {
        var pageId = await PageWithAsync(Stream());
        var compactor = CreateCompactor();
        (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        var before = (await ReadStoredAsync(pageId)).Summary;

        (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.NothingToDo);

        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
    }

    [Fact]
    public async Task The_text_of_the_page_is_stored_with_the_snapshot_and_its_title_and_version_are_left_alone()
    {
        var pageId = await PageWithAsync(Stream(0, 1));
        await using (var db = CreateContext())
        {
            var page = await db.Pages.SingleAsync(p => p.Id == pageId);
            page.Title = "Weekend";
            page.Version = 7;
            await db.SaveChangesAsync();
        }

        await CreateCompactor().CompactAsync(pageId, default);

        await using var read = CreateContext();
        var after = await read.Pages.AsNoTracking().SingleAsync(p => p.Id == pageId);
        after.ContentText.ShouldContain("Boodschappen");
        after.ContentText.ShouldContain("kaas, appel, thee");
        after.Title.ShouldBe("Weekend");
        after.Version.ShouldBe(7);
        after.UpdatedAt.ShouldBe(Start);
    }

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task A_crash_at_any_stage_leaves_the_database_exactly_as_it_was(CompactionStage crashAt, bool withSnapshot)
    {
        var stream = Stream();
        var pageId = await PageWithAsync(stream.Take(40));
        if (withSnapshot)
        {
            (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
            await AppendAsync(pageId, stream.Skip(40).Take(20));
        }

        var before = (await ReadStoredAsync(pageId)).Summary;
        Hooks.OnStage = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;

        await Should.ThrowAsync<SimulatedCrash>(() => CreateCompactor().CompactAsync(pageId, default));

        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);

        // And the next compaction works: the lock went with the transaction.
        Hooks.OnStage = null;
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
    }

    [Fact]
    public async Task Updates_that_arrive_during_a_compaction_stay_live_and_are_part_of_the_page()
    {
        var stream = Stream();
        var pageId = await PageWithAsync(stream.Take(50));
        Hooks.OnStage = async stage =>
        {
            if (stage == CompactionStage.Merged)
            {
                await AppendAsync(pageId, stream.Skip(50));
            }
        };

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);

        var stored = await ReadStoredAsync(pageId);
        stored.LiveSeqs.Length.ShouldBe(stream.Count - 50);
        stored.ArchivedSeqs.Length.ShouldBe(50);
        (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync(stream, default));
    }

    [Fact]
    public async Task Two_compactions_of_one_page_at_once_give_one_compaction_and_one_skip()
    {
        var pageId = await PageWithAsync(Stream());
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

        var first = CreateCompactor().CompactAsync(pageId, default);
        await reached.Task;
        var second = await CreateCompactor().CompactAsync(pageId, default);
        release.SetResult();

        second.ShouldBe(CompactionResult.Skipped);
        (await first).ShouldBe(CompactionResult.Compacted);
    }

    [Fact]
    public async Task Pages_do_not_block_each_other()
    {
        var first = await PageWithAsync(Stream());
        var second = await PageWithAsync(Stream(40, 2));
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

        var blocked = CreateCompactor().CompactAsync(first, default);
        await reached.Task;
        Hooks.OnStage = null;
        var other = await CreateCompactor().CompactAsync(second, default);
        release.SetResult();

        other.ShouldBe(CompactionResult.Compacted);
        (await blocked).ShouldBe(CompactionResult.Compacted);
    }

    [Fact]
    public async Task A_merge_whose_fingerprints_differ_is_never_committed_and_is_logged_as_an_error()
    {
        var pageId = await PageWithAsync(Stream());
        var before = (await ReadStoredAsync(pageId)).Summary;
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsCompaction([1], [2], [3], [4], "text"));

        var result = await CreateCompactor(codec).CompactAsync(pageId, default);

        result.ShouldBe(CompactionResult.Failed);
        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
        CompactorLog.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains(pageId.ToString()));
    }

    [Fact]
    public async Task A_sidecar_that_is_down_makes_the_compaction_unavailable_and_changes_nothing()
    {
        var pageId = await PageWithAsync(Stream());
        var before = (await ReadStoredAsync(pageId)).Summary;
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new YjsUnavailableException("down"));

        (await CreateCompactor(codec).CompactAsync(pageId, default)).ShouldBe(CompactionResult.Unavailable);

        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
    }

    [Fact]
    public async Task A_page_whose_log_holds_an_update_that_does_not_decode_fails_and_stays_as_it_was()
    {
        var pageId = await PageWithAsync(Stream(10, 1));
        await AppendAsync(pageId, [[0xFF, 0xFF, 0xFF, 0xFF]]);
        var before = (await ReadStoredAsync(pageId)).Summary;

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Failed);

        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
        CompactorLog.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains(pageId.ToString()));
    }

    [Fact]
    public async Task A_page_that_is_not_an_editor_document_compacts_with_an_empty_text()
    {
        var fixture = YjsFixture.Load("non-tiptap");
        var pageId = await PageWithAsync(fixture.UpdateBytes);

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);

        (await ReadStoredAsync(pageId)).Text.ShouldBe("");
        (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync(fixture.UpdateBytes, default));
    }

    [Fact]
    public async Task A_nul_character_in_the_text_does_not_stop_the_compaction()
    {
        // Postgres text cannot hold NUL, which a document can: it is dropped from the extract, not from the document.
        var pageId = await PageWithAsync(Stream(10, 1));
        var real = await Codec.CompactAsync(await PageInputsAsync(pageId), default);
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(real with { Text = "a\0b" });

        (await CreateCompactor(codec).CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);

        (await ReadStoredAsync(pageId)).Text.ShouldBe("ab");
    }

    private async Task<IReadOnlyList<byte[]>> PageInputsAsync(Guid pageId) => (await Store.LoadAsync(pageId, default)).Inputs;

    // ---- the full-history check --------------------------------------------------------------------------------------

    /// <summary>A page with two compactions behind it, so that the next one is version 3.</summary>
    private async Task<(Guid PageId, IReadOnlyList<byte[]> Stream)> PageWithHistoryAsync()
    {
        Options.FullHistoryEvery = 3;
        var stream = Stream(120, 3);
        var pageId = await PageWithAsync(stream.Take(40));
        var compactor = CreateCompactor();
        (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        await AppendAsync(pageId, stream.Skip(40).Take(40));
        (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        await AppendAsync(pageId, stream.Skip(80));
        return (pageId, stream);
    }

    /// <summary>Replaces the page's snapshot with the state of the first update only: a snapshot that lost the rest.</summary>
    private async Task TamperWithSnapshotAsync(Guid pageId, IReadOnlyList<byte[]> stream, bool historyComplete)
    {
        var wrong = await Codec.CompactAsync([stream[0]], default);
        await using var db = CreateContext();
        await db.PageSnapshots.Where(s => s.PageId == pageId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.State, wrong.State)
            .SetProperty(x => x.StateVector, wrong.StateVector)
            .SetProperty(x => x.HistoryComplete, historyComplete));
    }

    [Fact]
    public async Task The_full_history_check_passes_for_a_healthy_page()
    {
        var (pageId, stream) = await PageWithHistoryAsync();

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);

        (await ReadStoredAsync(pageId)).Snapshot!.Version.ShouldBe(3);
        (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync(stream, default));
    }

    [Fact]
    public async Task A_wrong_earlier_snapshot_is_caught_by_the_next_due_full_history_check()
    {
        var (pageId, stream) = await PageWithHistoryAsync();
        await TamperWithSnapshotAsync(pageId, stream, historyComplete: true);
        var before = (await ReadStoredAsync(pageId)).Summary;

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Failed);

        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
        CompactorLog.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.Message.Contains(pageId.ToString()));
    }

    [Fact]
    public async Task The_full_history_check_is_skipped_once_the_purge_has_taken_part_of_the_history()
    {
        var (pageId, stream) = await PageWithHistoryAsync();
        await TamperWithSnapshotAsync(pageId, stream, historyComplete: false);

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
    }

    [Fact]
    public async Task The_full_history_check_only_runs_every_so_many_versions()
    {
        var (pageId, stream) = await PageWithHistoryAsync();
        Options.FullHistoryEvery = 10;
        await TamperWithSnapshotAsync(pageId, stream, historyComplete: true);

        // Version 3 of 10: not due, so the tampered snapshot is not noticed yet.
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
    }

    // ---- commit order and large logs ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_row_with_a_lower_seq_that_commits_after_the_read_stays_live_and_is_part_of_the_page()
    {
        var stream = Stream(20, 4);
        var pageId = await AddPageAsync();

        // Two appends in flight at once: A is numbered first and commits last, after the compaction has read the log but
        // before it deletes from it. Deleting "everything up to the watermark" would take A with it, unmerged.
        await using var contextA = CreateContext();
        await using var contextB = CreateContext();
        await using var transactionA = await contextA.Database.BeginTransactionAsync();
        await using var transactionB = await contextB.Database.BeginTransactionAsync();
        var rowA = new PageUpdate { PageId = pageId, UserId = UserId, Data = stream[0], CreatedAt = Start };
        var rowB = new PageUpdate { PageId = pageId, UserId = UserId, Data = stream[1], CreatedAt = Start };
        contextA.PageUpdates.Add(rowA);
        await contextA.SaveChangesAsync();
        contextB.PageUpdates.Add(rowB);
        await contextB.SaveChangesAsync();
        await transactionB.CommitAsync();
        rowA.Seq.ShouldBeLessThan(rowB.Seq);

        Hooks.OnStage = async stage =>
        {
            if (stage == CompactionStage.Merged)
            {
                await transactionA.CommitAsync();
            }
        };
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        Hooks.OnStage = null;

        // A was not in the compaction's read, so it was neither merged nor deleted.
        var stored = await ReadStoredAsync(pageId);
        stored.LiveSeqs.ShouldBe([rowA.Seq]);
        stored.ArchivedSeqs.ShouldBe([rowB.Seq]);
        (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync([stream[0], stream[1]], default));

        // Compacting it afterwards does not move the watermark back.
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        stored = await ReadStoredAsync(pageId);
        stored.Snapshot!.Watermark.ShouldBe(rowB.Seq);
        stored.Snapshot.UpdateCount.ShouldBe(2);
        stored.LiveSeqs.ShouldBeEmpty();
        (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync([stream[0], stream[1]], default));
    }

    [Fact]
    public async Task A_long_log_is_split_over_several_compactions_with_the_same_result()
    {
        Options.MaxUpdatesPerCompaction = 50;
        var stream = Stream(130, 5);
        var pageId = await PageWithAsync(stream);

        var compactions = 0;
        while (await CreateCompactor().CompactAsync(pageId, default) == CompactionResult.Compacted)
        {
            compactions++;
        }

        compactions.ShouldBe((int)Math.Ceiling(stream.Count / 50.0));
        var stored = await ReadStoredAsync(pageId);
        stored.LiveSeqs.ShouldBeEmpty();
        stored.ArchivedSeqs.Length.ShouldBe(stream.Count);
        stored.Snapshot!.UpdateCount.ShouldBe(stream.Count);
        (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync(stream, default));
    }
}
