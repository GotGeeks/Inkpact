using System.Text;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.History;
using Inkpact.Api.Pages;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Inkpact.Api.Tests.History;

/// <summary>
/// Capture of versions (run B.5b), against real Postgres and the real Yjs sidecar: when a page is due, what a capture
/// writes, and what it leaves alone.
/// </summary>
public class HistoryCaptureTests(PostgresFixture postgres, YjsSidecarFixture yjs)
    : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    private static readonly byte[] EmptyDocument = """{"type":"doc"}"""u8.ToArray();

    private readonly HistoryOptions _options = new();
    private readonly CapturingLogger<HistoryCapture> _log = new();

    private HistoryCapture Capture(IYjsCodec? codec = null, IHistorySnapshotHooks? hooks = null) =>
        new(Factory, new HistorySnapshotBuilder(Factory, codec ?? Codec, Clock, hooks ?? new NoHistorySnapshotHooks()), _options, Clock, _log);

    private async Task<List<PageVersion>> VersionsAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToListAsync();
    }

    private async Task<PageHistoryState?> StateAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.PageHistoryStates.AsNoTracking().SingleOrDefaultAsync(s => s.PageId == pageId);
    }

    private async Task SetPolicyAsync(bool enabled = true, long? capBytes = null)
    {
        await using var db = CreateContext();
        await db.Workspaces.Where(w => w.Id == WorkspaceId).ExecuteUpdateAsync(w => w
            .SetProperty(x => x.HistoryEnabled, enabled)
            .SetProperty(x => x.HistoryCapBytes, capBytes));
    }

    /// <summary>A rename as the tree makes it: the title, the page's version, and the row for the capture worker.</summary>
    private async Task RenameAsync(Guid pageId, string title)
    {
        await using var db = CreateContext();
        var tree = new TreeService(db, Clock, NullLogger<TreeService>.Instance);
        (await tree.RenameAsync(pageId, title, null, default, changedBy: UserId)).Error.ShouldBeNull();
    }

    private async Task<Guid> EditedPageAsync(int edits = 12, int seed = 1)
    {
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, EditStreams.Get(edits, seed));
        return pageId;
    }

    // ---- when a page is due -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_that_was_edited_is_due_once_it_has_been_idle_and_not_before()
    {
        var pageId = await EditedPageAsync();

        Clock.Advance(_options.CaptureIdleAfter - TimeSpan.FromSeconds(1));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
    }

    [Fact]
    public async Task A_page_that_keeps_being_edited_is_due_once_its_oldest_change_is_five_minutes_old()
    {
        var pageId = await AddPageAsync();
        var stream = EditStreams.Get(8, 2);
        foreach (var update in stream)
        {
            // An edit a minute, so the page is never idle for two.
            await AppendAsync(pageId, [update]);
            Clock.Advance(TimeSpan.FromMinutes(1));
            if (Clock.GetUtcNow() - Start < _options.CaptureAtLatestAfter)
            {
                (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
            }
        }

        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
    }

    [Fact]
    public async Task A_page_nobody_wrote_to_is_never_due()
    {
        await AddPageAsync();
        Clock.Advance(TimeSpan.FromDays(1));

        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_whose_changes_are_all_compacted_is_due()
    {
        var pageId = await EditedPageAsync();
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        (await ReadStoredAsync(pageId)).LiveSeqs.ShouldBeEmpty();

        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
    }

    [Fact]
    public async Task A_page_with_a_long_log_is_left_to_the_compactor_first()
    {
        var pageId = await EditedPageAsync();
        _options.CaptureMaxLiveBytes = 10;
        Clock.Advance(_options.CaptureLargeLogAfter - TimeSpan.FromSeconds(1));

        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
    }

    [Fact]
    public async Task A_page_with_a_long_log_the_compactor_never_gets_to_is_captured_after_an_hour_and_so_is_its_rename()
    {
        var pageId = await EditedPageAsync();
        var renamed = await EditedPageAsync(4, 2);
        (await Capture().CaptureAsync(renamed, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        await AppendAsync(renamed, EditStreams.Get(8, 2).Skip(4));
        _options.CaptureMaxLiveBytes = 10;
        Clock.Advance(TimeSpan.FromMinutes(30));
        await RenameAsync(renamed, "Renamed");

        // Nobody compacts. Within the hour both wait, the rename too: it would be a replay of the same long log.
        Clock.Advance(TimeSpan.FromMinutes(29));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromMinutes(1));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId, renamed]);
        (await Capture().RunRoundAsync(default)).ShouldBe(2);
        (await VersionsAsync(pageId)).Count.ShouldBe(1);
        (await VersionsAsync(renamed)).Select(v => v.Kind).ShouldBe([PageVersionKind.Automatic, PageVersionKind.Rename]);
    }

    [Fact]
    public async Task A_page_typed_in_without_a_pause_is_due_after_five_minutes_also_when_the_compactor_keeps_taking_its_updates()
    {
        // An edit every half minute, and a compaction of everything live right before each: what is live is never older
        // than half a minute, and the page is never idle.
        var stream = EditStreams.Get(14, 3);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, [stream[0]]);
        for (var i = 1; i < stream.Count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(30));
            (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
            await AppendAsync(pageId, [stream[i]]);
            var due = await Capture().DuePagesAsync(default);
            if (Clock.GetUtcNow() - Start < _options.CaptureAtLatestAfter)
            {
                due.ShouldBeEmpty($"after {Clock.GetUtcNow() - Start}");
            }
            else
            {
                // The oldest change that is in no version is in the archive by now, and still counts.
                due.ShouldBe([pageId], $"after {Clock.GetUtcNow() - Start}");
            }
        }

        (await Capture().RunRoundAsync(default)).ShouldBe(1);
        (await VersionsAsync(pageId)).Count.ShouldBe(1);

        // From that version on the five minutes start again.
        Clock.Advance(TimeSpan.FromSeconds(30));
        await AppendAsync(pageId, EditStreams.Get(16, 3).Skip(14));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_round_takes_a_bounded_number_of_pages_the_oldest_change_first()
    {
        var first = await EditedPageAsync(4, 1);
        Clock.Advance(TimeSpan.FromMinutes(10));
        var second = await EditedPageAsync(4, 2);
        Clock.Advance(TimeSpan.FromMinutes(10));
        var third = await EditedPageAsync(4, 3);
        Clock.Advance(TimeSpan.FromMinutes(10));
        _options.CapturesPerRound = 2;

        (await Capture().DuePagesAsync(default)).ShouldBe([first, second]);
        (await Capture().RunRoundAsync(default)).ShouldBe(2);
        (await Capture().DuePagesAsync(default)).ShouldBe([third]);
    }

    [Fact]
    public async Task Nothing_is_due_in_a_workspace_whose_history_is_off()
    {
        await EditedPageAsync();
        await SetPolicyAsync(enabled: false);
        Clock.Advance(TimeSpan.FromHours(1));

        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    // ---- what a capture writes --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_capture_makes_one_version_that_is_the_whole_page_and_leaves_the_page_as_it_was()
    {
        var pageId = await EditedPageAsync();
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        await AppendAsync(pageId, EditStreams.Get(3, 9));
        var before = await ReadStoredAsync(pageId);
        var fingerprint = await StoredFingerprintAsync(pageId);
        Clock.Advance(TimeSpan.FromMinutes(3));

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        version.Kind.ShouldBe(PageVersionKind.Automatic);
        version.CreatedBy.ShouldBeNull();
        version.Title.ShouldBe("Page");
        version.CreatedAt.ShouldBe(Clock.GetUtcNow());
        version.Fingerprint.ShouldNotBeEmpty();
        // The version alone, read back, is the page.
        (await Codec.FingerprintAsync([version.State], default)).ShouldBe(await Codec.FingerprintAsync(
            [await Codec.BuildAsync("""{"type":"doc"}"""u8.ToArray(), default), .. (await Store.LoadAsync(pageId, default)).Inputs], default));
        version.ContentSeq.ShouldBe(before.LiveSeqs[^1]);
        (await StateAsync(pageId))!.CapturedSeq.ShouldBe(before.LiveSeqs[^1]);

        // Live updates, the snapshot and the archive are untouched.
        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before.Summary);
        (await StoredFingerprintAsync(pageId)).ShouldBe(fingerprint);
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_that_did_not_change_gets_no_second_version()
    {
        var pageId = await EditedPageAsync();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unchanged);

        (await VersionsAsync(pageId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_compaction_alone_makes_no_new_version()
    {
        var pageId = await EditedPageAsync();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);

        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unchanged);
        (await VersionsAsync(pageId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_page_that_changed_after_its_capture_is_due_again_and_gets_another_version()
    {
        var stream = EditStreams.Get(20, 5);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, stream.Take(10));
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        await AppendAsync(pageId, stream.Skip(10));
        Clock.Advance(TimeSpan.FromMinutes(3));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture().RunRoundAsync(default)).ShouldBe(1);

        var versions = await VersionsAsync(pageId);
        versions.Count.ShouldBe(2);
        versions[0].Fingerprint.ShouldNotBe(versions[1].Fingerprint);
        versions[1].ContentSeq.ShouldBeGreaterThan(versions[0].ContentSeq);
    }

    [Fact]
    public async Task A_page_nobody_wrote_to_can_be_captured_and_is_its_first_paragraph()
    {
        var pageId = await AddPageAsync();

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        version.ContentSeq.ShouldBe(0);
        (await Codec.FingerprintAsync([version.State], default))
            .ShouldBe(await Codec.FingerprintAsync([await Codec.BuildAsync(EmptyDocument, default)], default));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_imported_page_is_captured_with_its_content()
    {
        // As the import stores a note: one update built from the converted document, which starts from the first paragraph.
        var pageId = await AddPageAsync();
        var content = """{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"From Joplin"}]},{"type":"paragraph","content":[{"type":"text","text":"An imported line"}]}]}"""u8.ToArray();
        await AppendAsync(pageId, [await Codec.BuildAsync(content, default)]);
        Clock.Advance(TimeSpan.FromMinutes(3));

        (await Capture().RunRoundAsync(default)).ShouldBe(1);

        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        var read = await Codec.ReadAsync([version.State], default);
        read.Skipped.ShouldBe(0);
        var json = Encoding.UTF8.GetString(read.Json);
        json.ShouldContain("From Joplin");
        json.ShouldContain("An imported line");
    }

    [Fact]
    public async Task A_file_note_is_captured_with_its_search_text_which_counts_and_a_change_of_it_alone_is_a_new_version()
    {
        var pageId = await EditedPageAsync();
        await using (var db = CreateContext())
        {
            await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(p => p.SetProperty(x => x.FileText, "The text of the file"));
        }

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        version.FileText.ShouldBe("The text of the file");
        version.PayloadBytes.ShouldBe(version.State.Length + Encoding.UTF8.GetByteCount("The text of the file"));

        await using (var db = CreateContext())
        {
            await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(p => p.SetProperty(x => x.FileText, "Another text"));
        }

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        (await VersionsAsync(pageId)).Select(v => v.FileText).ShouldBe(["The text of the file", "Another text"]);
    }

    // ---- a capture racing what else writes the page ------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_stored_while_a_capture_is_under_way_is_not_in_its_version_and_gets_the_next()
    {
        var stream = EditStreams.Get(12, 4);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, stream.Take(8));
        var first = await Codec.BuildAsync(EmptyDocument, default);
        var hooks = new During(() => AppendAsync(pageId, stream.Skip(8)));

        (await Capture(hooks: hooks).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        // One observation: the version is the page as it was when the capture began.
        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        (await Codec.FingerprintAsync([version.State], default)).ShouldBe(await Codec.FingerprintAsync([first, .. stream.Take(8)], default));

        Clock.Advance(TimeSpan.FromMinutes(3));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture().RunRoundAsync(default)).ShouldBe(1);
        var newest = (await VersionsAsync(pageId))[^1];
        (await Codec.FingerprintAsync([newest.State], default)).ShouldBe(await Codec.FingerprintAsync([first, .. stream], default));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_compaction_in_batches_while_a_capture_is_under_way_changes_neither_the_version_nor_what_is_due()
    {
        var stream = EditStreams.Get(12, 6);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, stream);
        var first = await Codec.BuildAsync(EmptyDocument, default);
        Options.MaxUpdatesPerCompaction = 4;
        var hooks = new During(async () => (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted));

        (await Capture(hooks: hooks).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        // The first batch went into the snapshot meanwhile; the rest is still live.
        (await ReadStoredAsync(pageId)).LiveSeqs.Length.ShouldBe(8);
        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        (await Codec.FingerprintAsync([version.State], default)).ShouldBe(await Codec.FingerprintAsync([first, .. stream], default));

        Clock.Advance(TimeSpan.FromHours(1));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
        while (await CreateCompactor().CompactAsync(pageId, default) == CompactionResult.Compacted)
        {
        }

        (await ReadStoredAsync(pageId)).LiveSeqs.ShouldBeEmpty();
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unchanged);
    }

    [Fact]
    public async Task An_update_that_commits_late_below_what_was_captured_makes_the_page_due_and_is_in_the_next_version()
    {
        // Two people's first edits, stored by two transactions: the one with the lower number commits last.
        var early = EditStreams.Get(1, 21)[0];
        var late = EditStreams.Get(1, 22)[0];
        var pageId = await AddPageAsync();
        var first = await Codec.BuildAsync(EmptyDocument, default);
        await using var slow = CreateContext();
        await using var quick = CreateContext();
        await using var slowTransaction = await slow.Database.BeginTransactionAsync();
        var lateRow = new PageUpdate { PageId = pageId, UserId = UserId, Data = late, CreatedAt = Start };
        slow.PageUpdates.Add(lateRow);
        await slow.SaveChangesAsync();
        var earlyRow = new PageUpdate { PageId = pageId, UserId = UserId, Data = early, CreatedAt = Start };
        quick.PageUpdates.Add(earlyRow);
        await quick.SaveChangesAsync();
        lateRow.Seq.ShouldBeLessThan(earlyRow.Seq);

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        (await StateAsync(pageId))!.CapturedSeq.ShouldBe(earlyRow.Seq);
        var version = (await VersionsAsync(pageId)).ShouldHaveSingleItem();
        (await Codec.FingerprintAsync([version.State], default)).ShouldBe(await Codec.FingerprintAsync([first, early], default));

        await slowTransaction.CommitAsync();
        Clock.Advance(TimeSpan.FromMinutes(3));

        // Its number is below the captured one, so the number alone would never show it.
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture().RunRoundAsync(default)).ShouldBe(1);
        var newest = (await VersionsAsync(pageId))[^1];
        (await Codec.FingerprintAsync([newest.State], default)).ShouldBe(await Codec.FingerprintAsync([first, early, late], default));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Versions_do_not_depend_on_the_archive_or_on_a_later_compaction()
    {
        var pageId = await EditedPageAsync();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        var captured = (await VersionsAsync(pageId)).Single();

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);
        Clock.Advance(Options.ArchiveRetention + TimeSpan.FromDays(1));
        (await CreatePurge().PurgeAsync(default)).ShouldBeGreaterThan(0);

        var after = (await VersionsAsync(pageId)).Single();
        after.State.ShouldBe(captured.State);
        (await Codec.ReadAsync([after.State], default)).Skipped.ShouldBe(0);
    }

    // ---- renames ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_renames_leave_two_versions_each_with_its_title_and_who_renamed()
    {
        var pageId = await EditedPageAsync();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        Clock.Advance(TimeSpan.FromMinutes(1));
        await RenameAsync(pageId, "Second title");
        Clock.Advance(TimeSpan.FromSeconds(10));
        var secondAt = Clock.GetUtcNow();
        await RenameAsync(pageId, "Third title");

        // A rename makes a page due at once: there is no content to wait for.
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture().RunRoundAsync(default)).ShouldBe(1);

        var versions = await VersionsAsync(pageId);
        versions.Select(v => (v.Kind, v.Title)).ShouldBe(
        [
            (PageVersionKind.Automatic, "Page"),
            (PageVersionKind.Rename, "Second title"),
            (PageVersionKind.Rename, "Third title"),
        ]);
        versions[1].CreatedBy.ShouldBe(UserId);
        versions[2].CreatedAt.ShouldBe(secondAt);
        // The same body in all three: only the title changed.
        versions.Select(v => Convert.ToHexString(v.Fingerprint)).Distinct().Count().ShouldBe(1);
        await using var db = CreateContext();
        (await db.PageTitleChanges.CountAsync()).ShouldBe(0);
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rename_to_the_same_title_leaves_nothing_and_one_made_with_history_off_is_dropped_by_the_next_round()
    {
        var pageId = await AddPageAsync();
        await RenameAsync(pageId, "Page");
        await using (var db = CreateContext())
        {
            (await db.PageTitleChanges.CountAsync()).ShouldBe(0);
        }

        await SetPolicyAsync(enabled: false);
        await RenameAsync(pageId, "Renamed while off");

        (await Capture().RunRoundAsync(default)).ShouldBe(0);

        await using var after = CreateContext();
        (await after.PageTitleChanges.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_title_that_waited_when_history_was_turned_off_is_not_made_a_version_of_when_it_is_turned_on_again()
    {
        var pageId = await EditedPageAsync();
        var other = await EditedPageAsync(4, 2);
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        await RenameAsync(pageId, "Renamed before it was off");
        await SetPolicyAsync(enabled: false);

        // No page of the workspace is due, and the rounds still clear what waited.
        (await Capture().RunRoundAsync(default)).ShouldBe(0);
        await using (var db = CreateContext())
        {
            (await db.PageTitleChanges.CountAsync()).ShouldBe(0);
        }

        Clock.Advance(TimeSpan.FromDays(30));
        await SetPolicyAsync(enabled: true);

        // Only the page that has content in no version; the renamed one has nothing dated a month ago to add.
        (await Capture().DuePagesAsync(default)).ShouldBe([other]);
        (await VersionsAsync(pageId)).Select(v => v.Kind).ShouldBe([PageVersionKind.Automatic]);
    }

    [Fact]
    public async Task A_rename_that_commits_while_a_capture_is_under_way_waits_for_the_next_one()
    {
        var pageId = await EditedPageAsync();
        var hooks = new During(() => RenameAsync(pageId, "Renamed meanwhile"));

        (await Capture(hooks: hooks).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        // The capture observed the old title, and its version says so; the new title is still to come.
        (await VersionsAsync(pageId)).Select(v => v.Title).ShouldBe(["Page"]);
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        // Read by time, as the list is: the rename was made before the first version was written, and still comes after it.
        (await VersionsAsync(pageId)).Select(v => (v.Kind, v.Title)).ShouldBe(
            [(PageVersionKind.Automatic, "Page"), (PageVersionKind.Rename, "Renamed meanwhile")]);
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unchanged);
        (await VersionsAsync(pageId)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_title_that_waits_stays_through_a_capture_that_fails_and_gets_its_version_from_the_next()
    {
        var pageId = await EditedPageAsync();
        await RenameAsync(pageId, "Renamed");
        var down = Substitute.For<IYjsCodec>();
        down.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new YjsUnavailableException("down"));

        (await Capture(down).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.SidecarUnavailable);

        (await VersionsAsync(pageId)).ShouldBeEmpty();
        await using (var db = CreateContext())
        {
            (await db.PageTitleChanges.Select(c => c.Title).ToListAsync()).ShouldBe(["Renamed"]);
        }

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        (await VersionsAsync(pageId)).Select(v => (v.Kind, v.Title)).ShouldBe([(PageVersionKind.Rename, "Renamed")]);
        await using var after = CreateContext();
        (await after.PageTitleChanges.CountAsync()).ShouldBe(0);
    }

    /// <summary>Does something once, after a capture has read the page's row and before it reads the content.</summary>
    private sealed class During(Func<Task> rename) : IHistorySnapshotHooks
    {
        private bool _done;

        public async Task AfterMetadataReadAsync(Guid pageId, CancellationToken cancellationToken)
        {
            if (!_done)
            {
                _done = true;
                await rename();
            }
        }
    }

    // ---- the policy and the cap -------------------------------------------------------------------------------------------

    [Fact]
    public async Task History_turned_off_while_a_capture_is_under_way_gets_no_version_and_drops_the_titles_that_waited()
    {
        var pageId = await EditedPageAsync();
        await RenameAsync(pageId, "Renamed");
        var hooks = new During(() => SetPolicyAsync(enabled: false));

        (await Capture(hooks: hooks).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Off);

        (await VersionsAsync(pageId)).ShouldBeEmpty();
        await using var db = CreateContext();
        (await db.PageTitleChanges.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Under_a_cap_the_oldest_unprotected_versions_make_room_and_protected_ones_stay()
    {
        var pageId = await EditedPageAsync();
        var other = await AddPageAsync();
        long size;
        await using (var db = CreateContext())
        {
            db.PageVersions.AddRange(
                new PageVersion(other, new byte[400], "Oldest", "", Start.AddDays(-3), null, PageVersionKind.Automatic),
                new PageVersion(other, new byte[400], "Protected", "", Start.AddDays(-2), null, PageVersionKind.BeforeRestore, protectedUntil: Start.AddDays(1)),
                new PageVersion(other, new byte[400], "Newer", "", Start.AddDays(-1), null, PageVersionKind.Automatic));
            await db.SaveChangesAsync();
        }

        // Room for the two that must stay and the new one, not for all four.
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        size = (await VersionsAsync(pageId)).Single().PayloadBytes;
        await using (var db = CreateContext())
        {
            await db.PageVersions.Where(v => v.PageId == pageId).ExecuteDeleteAsync();
            await db.PageHistoryStates.ExecuteDeleteAsync();
        }

        await SetPolicyAsync(capBytes: 800 + size);

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        (await VersionsAsync(other)).Select(v => v.Title).ShouldBe(["Protected", "Newer"]);
        (await VersionsAsync(pageId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Under_a_cap_that_fits_one_version_several_titles_that_waited_leave_the_newest_and_the_page_is_not_full()
    {
        var pageId = await EditedPageAsync();
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        var size = (await VersionsAsync(pageId)).Single().PayloadBytes;
        await RenameAsync(pageId, "Second");
        await RenameAsync(pageId, "Third");
        await RenameAsync(pageId, "Fourth");
        Clock.Advance(TimeSpan.FromMinutes(1));

        // Room for one copy of the page and a half, not for the three that the renames would be.
        await SetPolicyAsync(capBytes: size + size / 2);

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);

        // The earliest titles went, as the oldest versions do under a cap; so did the first version, to make room.
        (await VersionsAsync(pageId)).Select(v => (v.Kind, v.Title)).ShouldBe([(PageVersionKind.Rename, "Fourth")]);
        (await StateAsync(pageId))!.Failure.ShouldBeNull();
        await using var db = CreateContext();
        (await db.PageTitleChanges.CountAsync()).ShouldBe(0);
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cap_that_leaves_no_room_writes_nothing_removes_nothing_and_says_full()
    {
        var pageId = await EditedPageAsync();
        var other = await AddPageAsync();
        await using (var db = CreateContext())
        {
            db.PageVersions.AddRange(
                new PageVersion(other, new byte[300], "Unprotected", "", Start.AddDays(-3), null, PageVersionKind.Automatic),
                new PageVersion(other, new byte[300], "Protected", "", Start.AddDays(-2), null, PageVersionKind.BeforeRestore, protectedUntil: Start.AddDays(1)));
            await db.SaveChangesAsync();
        }

        // Even without the unprotected one, the protected one and the new version do not fit.
        await SetPolicyAsync(capBytes: 310);

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Full);

        (await VersionsAsync(pageId)).ShouldBeEmpty();
        (await VersionsAsync(other)).Select(v => v.Title).ShouldBe(["Unprotected", "Protected"]);
        var state = (await StateAsync(pageId)).ShouldNotBeNull();
        state.Failure.ShouldBe(PageHistoryFailures.Full);
        state.CapturedSeq.ShouldBe(0);
        state.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromHours(1));
        _log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains(pageId.ToString()));

        // With room again, the next attempt captures and the note goes.
        await SetPolicyAsync(capBytes: null);
        Clock.Advance(TimeSpan.FromHours(1));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        (await StateAsync(pageId))!.Failure.ShouldBeNull();
    }

    // ---- failures ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_that_cannot_be_read_back_gets_no_version_and_waits_longer_each_time()
    {
        var pageId = await EditedPageAsync();
        Clock.Advance(TimeSpan.FromMinutes(3));
        var codec = Substitute.For<IYjsCodec>();
        codec.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        // The two fingerprints differ: the state does not match its replay.
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsCompaction([1], [1], [1], [2], "", []));

        (await Capture(codec).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unreadable);

        (await VersionsAsync(pageId)).ShouldBeEmpty();
        var state = (await StateAsync(pageId)).ShouldNotBeNull();
        (state.Failure, state.FailedAttempts, state.CapturedSeq).ShouldBe((PageHistoryFailures.Unreadable, 1, 0));
        state.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromHours(1));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromHours(1));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);
        (await Capture(codec).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unreadable);
        (await StateAsync(pageId))!.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromHours(2));

        // The log names the page and the kind of failure, and nothing of the page.
        _log.Entries.Where(e => e.Level == LogLevel.Warning).ShouldAllBe(e => e.Message.Contains(pageId.ToString()));
    }

    [Fact]
    public async Task A_page_that_could_not_be_read_is_tried_again_once_it_was_written_to_and_not_before()
    {
        var stream = EditStreams.Get(12, 8);
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, stream.Take(8));
        var codec = Substitute.For<IYjsCodec>();
        codec.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsCompaction([1], [1], [1], [2], "", []));
        (await Capture(codec).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unreadable);

        // Nothing changed: it waits its hour.
        Clock.Advance(TimeSpan.FromMinutes(30));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();

        // Someone writes to it, which may have taken out what could not be read: the page is tried again, as any page
        // whose oldest change in no version is more than five minutes old.
        await AppendAsync(pageId, stream.Skip(8));
        (await Capture().DuePagesAsync(default)).ShouldBe([pageId]);

        // Still unreadable: it waits again, from now, and the same content does not bring it back.
        (await Capture(codec).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unreadable);
        Clock.Advance(TimeSpan.FromMinutes(30));
        (await Capture().DuePagesAsync(default)).ShouldBeEmpty();

        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        var state = (await StateAsync(pageId)).ShouldNotBeNull();
        (state.Failure, state.FailedAt, state.FailedAttempts, state.NextAttemptAt).ShouldBe((null, null, 0, null));
    }

    [Fact]
    public async Task A_sidecar_that_is_down_neither_forgets_that_a_page_could_not_be_read_nor_makes_it_wait_longer_for_that()
    {
        var pageId = await EditedPageAsync();
        var down = Substitute.For<IYjsCodec>();
        down.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new YjsUnavailableException("down"));
        var broken = Substitute.For<IYjsCodec>();
        broken.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        broken.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(new YjsCompaction([1], [1], [1], [2], "", []));

        // Five times the sidecar is down, each wait twice the one before.
        for (var i = 0; i < 5; i++)
        {
            (await Capture(down).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.SidecarUnavailable);
        }

        var waiting = (await StateAsync(pageId)).ShouldNotBeNull();
        (waiting.Failure, waiting.FailedAttempts, waiting.WaitingAttempts).ShouldBe((null, 0, 5));
        waiting.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromMinutes(16));

        // The first time it cannot be read, it waits the first hour, not the thirty-second.
        (await Capture(broken).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Unreadable);
        var failed = (await StateAsync(pageId)).ShouldNotBeNull();
        (failed.Failure, failed.FailedAttempts).ShouldBe((PageHistoryFailures.Unreadable, 1));
        failed.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromHours(1));

        // And the sidecar being down after that leaves the page what it is: one that cannot be read.
        (await Capture(down).CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.SidecarUnavailable);
        var after = (await StateAsync(pageId)).ShouldNotBeNull();
        (after.Failure, after.FailedAttempts, after.FailedAt).ShouldBe((PageHistoryFailures.Unreadable, 1, Clock.GetUtcNow()));
        after.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task A_sidecar_that_is_down_ends_the_round_and_marks_no_page_as_failed()
    {
        var first = await EditedPageAsync(4, 1);
        Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await EditedPageAsync(4, 2);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var codec = Substitute.For<IYjsCodec>();
        codec.BuildAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new YjsUnavailableException("down"));

        (await Capture(codec).RunRoundAsync(default)).ShouldBe(1);

        var state = (await StateAsync(first)).ShouldNotBeNull();
        state.Failure.ShouldBeNull();
        state.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        (await StateAsync(second)).ShouldBeNull();
        // The page that met it waits a minute; the other is first in line now.
        (await Capture().DuePagesAsync(default)).ShouldBe([second]);
        (await Capture().RunRoundAsync(default)).ShouldBe(1);
        Clock.Advance(TimeSpan.FromMinutes(1));
        (await Capture().RunRoundAsync(default)).ShouldBe(1);
        (await VersionsAsync(first)).Count.ShouldBe(1);
        (await StateAsync(first))!.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task A_page_whose_capture_fails_for_a_reason_nobody_foresaw_waits_and_the_round_goes_on()
    {
        var first = await EditedPageAsync(4, 1);
        Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await EditedPageAsync(4, 2);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var calls = 0;
        var codec = Substitute.For<IYjsCodec>();
        codec.BuildAsync(default, default).ReturnsForAnyArgs(c => Codec.BuildAsync(c.Arg<ReadOnlyMemory<byte>>(), default));
        codec.ReadAsync(default!, default).ReturnsForAnyArgs(c => Codec.ReadAsync(c.Arg<IReadOnlyList<byte[]>>(), default));
        codec.CompactAsync(default!, default).ReturnsForAnyArgs(c => ++calls == 1
            ? throw new InvalidOperationException("ZQXMARKER what the page says")
            : Codec.CompactAsync(c.Arg<IReadOnlyList<byte[]>>(), default));

        (await Capture(codec).RunRoundAsync(default)).ShouldBe(2);

        (await VersionsAsync(first)).ShouldBeEmpty();
        (await VersionsAsync(second)).Count.ShouldBe(1);
        var state = (await StateAsync(first)).ShouldNotBeNull();
        state.Failure.ShouldBeNull();
        state.NextAttemptAt.ShouldBe(Clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        var error = _log.Entries.Single(e => e.Level == LogLevel.Error);
        error.Message.ShouldContain(first.ToString());
        error.Message.ShouldNotContain("ZQXMARKER");

        Clock.Advance(TimeSpan.FromMinutes(1));
        (await Capture().RunRoundAsync(default)).ShouldBe(1);
        (await VersionsAsync(first)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_page_that_is_gone_is_passed_over()
    {
        (await Capture().CaptureAsync(Guid.NewGuid(), default)).ShouldBe(HistoryCaptureOutcome.Gone);
    }

    [Fact]
    public async Task Deleting_a_page_takes_its_versions_cursor_and_waiting_titles_and_deleting_a_user_takes_none()
    {
        var pageId = await EditedPageAsync();
        await RenameAsync(pageId, "Renamed");
        (await Capture().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        await RenameAsync(pageId, "Renamed again");
        await using var db = CreateContext();
        var renamer = new User { Email = "renamer@example.com", DisplayName = "Renamer", CreatedAt = Start };
        db.Users.Add(renamer);
        await db.SaveChangesAsync();
        await db.PageTitleChanges.ExecuteUpdateAsync(c => c.SetProperty(x => x.ChangedBy, renamer.Id));

        await db.Users.Where(u => u.Id == renamer.Id).ExecuteDeleteAsync();
        (await db.PageTitleChanges.Select(c => c.ChangedBy).SingleAsync()).ShouldBeNull();
        // The one version is the rename's: with the same body, the page as it is now would be that version again.
        (await db.PageVersions.CountAsync()).ShouldBe(1);

        await db.Pages.Where(p => p.Id == pageId).ExecuteDeleteAsync();
        (await db.PageVersions.CountAsync()).ShouldBe(0);
        (await db.PageHistoryStates.CountAsync()).ShouldBe(0);
        (await db.PageTitleChanges.CountAsync()).ShouldBe(0);
    }

    // ---- generated schedules ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Edits, compactions (in small batches), renames and captures in a generated order. After every capture, the newest
    /// version is the page as a full replay of everything appended so far gives it, with the title it has; and at the end
    /// the page itself is that replay too: capturing changed nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Generated_edits_compactions_renames_and_captures_always_capture_the_full_replay(int seed)
    {
        var random = new Random(seed);
        var stream = EditStreams.Get(60, seed);
        var pageId = await AddPageAsync();
        var first = await Codec.BuildAsync("""{"type":"doc"}"""u8.ToArray(), default);
        var appended = new List<byte[]>();
        var title = "Page";
        var titles = new List<string>();
        Options.MaxUpdatesPerCompaction = 4;
        var next = 0;

        for (var step = 0; step < 40; step++)
        {
            Clock.Advance(TimeSpan.FromSeconds(random.Next(1, 90)));
            switch (random.Next(10))
            {
                case < 5 when next < stream.Count:
                    var count = Math.Min(random.Next(1, 4), stream.Count - next);
                    await AppendAsync(pageId, stream.Skip(next).Take(count));
                    appended.AddRange(stream.Skip(next).Take(count));
                    next += count;
                    break;
                case < 7:
                    await CreateCompactor().CompactAsync(pageId, default);
                    break;
                case < 8:
                    title = $"Title {step}";
                    titles.Add(title);
                    await RenameAsync(pageId, title);
                    break;
                default:
                    var outcome = await Capture().CaptureAsync(pageId, default);
                    outcome.ShouldBeOneOf(HistoryCaptureOutcome.Captured, HistoryCaptureOutcome.Unchanged);
                    var versions = await VersionsAsync(pageId);
                    if (versions.Count > 0)
                    {
                        var newest = versions.OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).Last();
                        newest.Title.ShouldBe(title, $"seed {seed}, step {step}");
                        (await Codec.FingerprintAsync([newest.State], default))
                            .ShouldBe(await Codec.FingerprintAsync([first, .. appended], default), $"seed {seed}, step {step}");
                    }

                    break;
            }
        }

        (await Capture().CaptureAsync(pageId, default)).ShouldBeOneOf(HistoryCaptureOutcome.Captured, HistoryCaptureOutcome.Unchanged);
        var all = await VersionsAsync(pageId);
        // Every title the page was given is in a version.
        titles.ShouldBeSubsetOf(all.Select(v => v.Title));
        if (appended.Count > 0)
        {
            (await StoredFingerprintAsync(pageId)).ShouldBe(await Codec.FingerprintAsync(appended, default));
        }
    }
}
