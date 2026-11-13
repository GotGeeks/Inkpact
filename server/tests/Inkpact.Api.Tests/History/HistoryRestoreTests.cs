using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.History;
using Inkpact.Api.Pages;
using Inkpact.Api.Permissions;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inkpact.Api.Tests.History;

/// <summary>
/// Restoring a version (run B.5b, PR 4), against real Postgres, the real Yjs sidecar and the real hub: what a restore
/// writes, that every refusal and every crash writes nothing, the operation id, and what a client that has the page open
/// receives.
/// </summary>
public class HistoryRestoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] EmptyDocument = """{"type":"doc"}"""u8.ToArray();

    private readonly MutableTimeProvider _clock = new(Start);
    private InkpactFactory _factory = null!;
    private SyncUser _owner = null!;
    private Guid _workspaceId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: _clock, sidecar: true);
        _owner = await SyncUser.SetUpAdministratorAsync(_factory);
        await Services<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ---- a restore --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_restore_puts_back_the_content_and_title_and_keeps_the_page_as_it_was_in_a_protected_version()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        var contentBefore = await ContentAsync(page.Id);
        var restoredAt = _clock.GetUtcNow();

        var response = await RestoreAsync(page.Id, page.First, mark);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await response.Content.ReadFromJsonAsync<RestoreAnswer>()).ShouldNotBeNull();
        answer.Title.ShouldBe("First title");

        // The content is the version's, the title too, with a new metadata version.
        (await ContentAsync(page.Id)).ShouldBe(await VersionContentAsync(page.First));
        var row = await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Id == page.Id));
        (row.Title, row.Version).ShouldBe(("First title", answer.Version));
        row.Version.ShouldBe(mark.Version + 1);
        // Search reads the version's text.
        var firstState = await UseDbAsync(db => db.PageVersions.Where(v => v.Id == page.First).Select(v => v.State).SingleAsync());
        row.ContentText.ShouldBe((await Codec.CompactAsync([firstState], default)).Text.Replace("\0", ""));

        // The page as it was: kept, protected for a day, made by the person who restored; then the restore itself.
        var versions = await VersionsAsync(page.Id);
        var before = versions.Single(v => v.Id == answer.BeforeVersionId);
        (before.Kind, before.Title, before.CreatedBy).ShouldBe((PageVersionKind.BeforeRestore, "Second title", _owner.UserId));
        before.ProtectedUntil.ShouldBe(restoredAt + HistoryRestore.ProtectFor);
        (await StateContentAsync(before.State)).ShouldBe(contentBefore);
        var restored = versions.Single(v => v.Id == answer.RestoreVersionId);
        (restored.Kind, restored.Title, restored.CreatedBy).ShouldBe((PageVersionKind.Restore, "First title", _owner.UserId));
        (await StateContentAsync(restored.State)).ShouldBe(await ContentAsync(page.Id));
        versions.Select(v => v.Id).TakeLast(2).ShouldBe([before.Id, restored.Id]);

        // The capture has nothing to add: the restore's version is the page.
        (await Services<HistoryCapture>().CaptureAsync(page.Id, default)).ShouldBe(HistoryCaptureOutcome.Unchanged);
        (await VersionsAsync(page.Id)).Count.ShouldBe(versions.Count);
    }

    [Fact]
    public async Task The_page_as_it_was_before_a_restore_can_itself_be_restored()
    {
        var page = await PageWithTwoVersionsAsync();
        var contentBefore = await ContentAsync(page.Id);
        var first = await (await RestoreAsync(page.Id, page.First, await MarkAsync(page.Id))).Content.ReadFromJsonAsync<RestoreAnswer>();
        _clock.Advance(TimeSpan.FromMinutes(1));

        var response = await RestoreAsync(page.Id, first!.BeforeVersionId, await MarkAsync(page.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ContentAsync(page.Id)).ShouldBe(contentBefore);
        (await UseDbAsync(db => db.Pages.Where(p => p.Id == page.Id).Select(p => p.Title).SingleAsync())).ShouldBe("Second title");
    }

    [Fact]
    public async Task A_restore_keeps_the_page_s_place_tags_and_favorite_and_does_not_touch_the_title_when_it_is_the_same()
    {
        var page = await PageWithTwoVersionsAsync();
        // Back to the second title, so a version with that title is restored and the title does not change.
        (await _owner.Http.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "First title" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _owner.Http.PostJsonAsync($"/api/pages/{page.Id}/tags", new { add = new[] { "kept" }, remove = Array.Empty<Guid>() })).IsSuccessStatusCode.ShouldBeTrue();
        var before = await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Id == page.Id));
        var mark = await MarkAsync(page.Id);

        (await RestoreAsync(page.Id, page.First, mark)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Id == page.Id));
        (after.ParentId, after.SortKey, after.Version, after.Title).ShouldBe((before.ParentId, before.SortKey, before.Version, before.Title));
        (await UseDbAsync(db => db.PageTags.CountAsync(t => t.PageId == page.Id))).ShouldBe(1);
    }

    [Fact]
    public async Task A_restore_sets_the_page_s_links_search_text_and_file_text_from_the_version()
    {
        var other = await CreatePageAsync("Linked page");
        var pageId = await CreatePageAsync("Page");
        var linked = $$$$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"see there","marks":[{"type":"link","attrs":{"href":"/p/{{{{other}}}}"}}]}]}]}""";
        var version = await AddVersionAsync(pageId, await Codec.BuildAsync(Encoding.UTF8.GetBytes(linked), default), "Page", "The file's own text");
        await AppendAsync(pageId, EditStreams.Get(4, 3));

        (await RestoreAsync(pageId, version, await MarkAsync(pageId))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = await UseDbAsync(db => db.Pages.AsNoTracking().SingleAsync(p => p.Id == pageId));
        row.ContentText.ShouldContain("see there");
        row.FileText.ShouldBe("The file's own text");
        (await UseDbAsync(db => db.PageLinks.Where(l => l.SourcePageId == pageId).Select(l => l.TargetPageId).ToListAsync())).ShouldBe([other]);
    }

    [Fact]
    public async Task Titles_that_waited_for_the_capture_become_versions_before_the_page_as_it_was()
    {
        var page = await PageWithTwoVersionsAsync();
        _clock.Advance(TimeSpan.FromSeconds(10));
        (await _owner.Http.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "Third title" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        _clock.Advance(TimeSpan.FromSeconds(10));
        (await _owner.Http.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "Fourth title" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var mark = await MarkAsync(page.Id);

        (await RestoreAsync(page.Id, page.First, mark)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await VersionsAsync(page.Id)).Select(v => (v.Kind, v.Title)).TakeLast(4).ShouldBe(
        [
            (PageVersionKind.Rename, "Third title"),
            (PageVersionKind.Rename, "Fourth title"),
            (PageVersionKind.BeforeRestore, "Fourth title"),
            (PageVersionKind.Restore, "First title"),
        ]);
        (await UseDbAsync(db => db.PageTitleChanges.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Everyone_who_has_the_page_open_is_told_it_was_replaced_and_gets_the_replacement_as_a_sync()
    {
        var page = await PageWithTwoVersionsAsync();
        await using var client = await _owner.ConnectAsync();
        var joined = await client.JoinAsync(page.Id);
        var mark = await MarkAsync(page.Id);

        (await RestoreAsync(page.Id, page.First, mark)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.ReceiveAsync()).Type.ShouldBe(MessageType.Replaced);
        var sync = await client.ReceiveAsync();
        sync.Type.ShouldBe(MessageType.Sync);
        // What the client had and what it got is the page as stored.
        var first = await Codec.BuildAsync(EmptyDocument, default);
        (await Codec.FingerprintAsync([first, .. joined, .. sync.Updates], default))
            .ShouldBe(await Codec.FingerprintAsync([first, .. (await StoredAsync(page.Id)).Inputs], default));
    }

    // ---- refusals write nothing -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_stored_since_the_list_was_read_refuses_the_restore()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        await AppendAsync(page.Id, EditStreams.Get(1, 77));

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, page.First, mark), HttpStatusCode.Conflict, "page_changed");
    }

    [Fact]
    public async Task A_rename_or_a_move_since_the_list_was_read_refuses_the_restore()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        (await _owner.Http.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "Renamed meanwhile" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, page.First, mark), HttpStatusCode.Conflict, "page_changed");

        var parent = await CreatePageAsync("Parent");
        mark = await MarkAsync(page.Id);
        (await _owner.Http.PostJsonAsync($"/api/pages/{page.Id}/move", new { parentId = parent })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, page.First, mark), HttpStatusCode.Conflict, "page_changed");
    }

    [Fact]
    public async Task A_page_in_the_trash_is_restored_from_the_trash_first()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        (await _owner.Http.PostJsonAsync($"/api/pages/{page.Id}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, page.First, mark), HttpStatusCode.Conflict, "page_in_trash");
    }

    [Fact]
    public async Task With_history_off_nothing_is_restored()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        await UseDbAsync(db => db.Workspaces.ExecuteUpdateAsync(w => w.SetProperty(x => x.HistoryEnabled, false)));

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, page.First, mark), HttpStatusCode.Conflict, "history_off");
    }

    [Fact]
    public async Task A_cap_without_room_for_the_page_as_it_was_refuses_and_removes_nothing()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        // Room for what is there, protected, and not for two more copies of the page.
        await UseDbAsync(db => db.PageVersions.ExecuteUpdateAsync(v => v.SetProperty(x => x.ProtectedUntil, Start.AddDays(1))));
        var size = await UseDbAsync(db => db.PageVersions.SumAsync(v => v.PayloadBytes));
        await UseDbAsync(db => db.Workspaces.ExecuteUpdateAsync(w => w.SetProperty(x => x.HistoryCapBytes, size + 1)));

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, page.First, mark), HttpStatusCode.Conflict, "history_full");
    }

    [Fact]
    public async Task Under_a_cap_the_oldest_unprotected_versions_make_room_for_the_restore()
    {
        var page = await PageWithTwoVersionsAsync();
        var other = await CreatePageAsync("Other");
        var old = await AddVersionAsync(other, await Codec.BuildAsync(EmptyDocument, default), "Old", new string('x', 4000), Start.AddDays(-10));
        var size = await UseDbAsync(db => db.PageVersions.SumAsync(v => v.PayloadBytes));
        var pageBytes = await UseDbAsync(db => db.PageVersions.Where(v => v.Id == page.First).Select(v => v.PayloadBytes).SingleAsync());
        await UseDbAsync(db => db.Workspaces.ExecuteUpdateAsync(w => w.SetProperty(x => x.HistoryCapBytes, size - 4000 + 4 * pageBytes)));

        (await RestoreAsync(page.Id, page.First, await MarkAsync(page.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await UseDbAsync(db => db.PageVersions.AnyAsync(v => v.Id == old))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_version_of_another_page_or_one_that_was_pruned_is_not_found()
    {
        var page = await PageWithTwoVersionsAsync();
        var other = await CreatePageAsync("Other");
        var foreign = await AddVersionAsync(other, await Codec.BuildAsync(EmptyDocument, default), "Other", "");
        var mark = await MarkAsync(page.Id);

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, foreign, mark), HttpStatusCode.NotFound, "version_not_found");
        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, Guid.NewGuid(), mark), HttpStatusCode.NotFound, "version_not_found");
    }

    [Fact]
    public async Task A_version_that_cannot_be_read_back_whole_is_not_restored_in_part()
    {
        var page = await PageWithTwoVersionsAsync();
        var broken = await AddVersionAsync(page.Id, [0xff, 0xff, 0xff, 0xff, 0x7f], "Broken", "");
        var mark = await MarkAsync(page.Id);

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, broken, mark), HttpStatusCode.Conflict, "version_unreadable");
    }

    [Fact]
    public async Task A_version_with_a_picture_the_page_no_longer_has_is_not_restored()
    {
        var page = await PageWithTwoVersionsAsync();
        var missing = Guid.NewGuid();
        var withPicture = $$$$"""{"type":"doc","content":[{"type":"image","attrs":{"attachmentId":"{{{{missing}}}}","alt":"gone"}}]}""";
        var version = await AddVersionAsync(page.Id, await Codec.BuildAsync(Encoding.UTF8.GetBytes(withPicture), default), "Picture", "");
        var mark = await MarkAsync(page.Id);

        await ExpectRefusedAsync(page.Id, () => RestoreAsync(page.Id, version, mark), HttpStatusCode.Conflict, "attachment_missing");
    }

    [Fact]
    public void The_files_a_version_names_are_found_at_any_depth_once_each()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var json = $$$$"""
            {"type":"doc","content":[
              {"type":"image","attrs":{"attachmentId":"{{{{a}}}}"}},
              {"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","content":[
                {"type":"paragraph","content":[{"type":"attachment","attrs":{"attachmentId":"{{{{b}}}}","fileName":"f"}},
                {"type":"attachment","attrs":{"attachmentId":"{{{{a}}}}"}}]}]}]}]},
              {"type":"image","attrs":{"attachmentId":"not an id"}}]}
            """;

        HistoryRestore.AttachmentIds(Encoding.UTF8.GetBytes(json)).Order().ShouldBe(new[] { a, b }.Order());
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("a.b")]
    [InlineData("-1.2")]
    public async Task A_request_without_a_readable_mark_is_refused(string mark)
    {
        var page = await PageWithTwoVersionsAsync();
        var version = await UseDbAsync(db => db.Pages.Where(p => p.Id == page.Id).Select(p => p.Version).SingleAsync());

        await ExpectRefusedAsync(
            page.Id,
            () => _owner.Http.PostJsonAsync(RestorePath(page.Id, page.First), new { operationId = Guid.NewGuid(), expectedContent = mark, expectedVersion = version }),
            HttpStatusCode.BadRequest,
            "restore_invalid");
    }

    [Fact]
    public async Task A_restore_that_would_make_a_page_too_large_to_open_is_refused()
    {
        await using var small = new InkpactFactory(
            await postgres.CreateDatabaseAsync(), time: _clock, sidecar: true,
            settings: new Dictionary<string, string>
            {
                ["Sync:MaxQueuedBytes"] = "4096",
                // The import's limits must fit what a page may be, or the app does not start.
                ["Import:MaxNoteTextBytes"] = "1000",
                ["Import:MaxNoteUpdateBytes"] = "1000",
            });
        var owner = await SyncUser.SetUpAdministratorAsync(small);
        await small.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        Guid pageId;
        Guid version;
        await using (var scope = small.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var page = new Page { WorkspaceId = await db.Workspaces.Select(w => w.Id).SingleAsync(), Title = "Page", SortKey = "a0", CreatedAt = Start, UpdatedAt = Start };
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            pageId = page.Id;
            var large = $$$$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"{{{{new string('x', 5000)}}}}"}]}]}""";
            var state = await small.Services.GetRequiredService<IYjsCodec>().BuildAsync(Encoding.UTF8.GetBytes(large), default);
            var row = new PageVersion(pageId, state, "Large", "", Start, null, PageVersionKind.Automatic);
            db.PageVersions.Add(row);
            await db.SaveChangesAsync();
            version = row.Id;
        }

        var list = await owner.Http.GetFromJsonAsync<VersionList>($"/api/pages/{pageId}/versions");
        var response = await owner.Http.PostJsonAsync(
            RestorePath(pageId, version), new { operationId = Guid.NewGuid(), expectedContent = list!.PageContent, expectedVersion = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadErrorAsync()).Code.ShouldBe("content_too_large");
    }

    // ---- crashes and races write nothing ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(RestoreStage.Prepared))]
    [InlineData(nameof(RestoreStage.Locked))]
    [InlineData(nameof(RestoreStage.BeforeWritten))]
    [InlineData(nameof(RestoreStage.UpdateStored))]
    [InlineData(nameof(RestoreStage.PageWritten))]
    public async Task A_restore_cut_short_at_any_point_writes_nothing_and_relays_nothing(string stageName)
    {
        var stage = Enum.Parse<RestoreStage>(stageName);
        var page = await PageWithTwoVersionsAsync();
        (await _owner.Http.PatchJsonAsync($"/api/pages/{page.Id}", new { title = "Waiting title" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var mark = await MarkAsync(page.Id);
        await using var client = await _owner.ConnectAsync();
        await client.JoinAsync(page.Id);
        var before = await SummaryAsync();

        var crash = new Hooks(at => at == stage ? throw new SimulatedCrash() : Task.CompletedTask);
        await Should.ThrowAsync<SimulatedCrash>(() => RestoreDirectAsync(page.Id, page.First, mark, _owner.UserId, crash));

        (await SummaryAsync()).ShouldBe(before);
        await client.ExpectSilenceAsync();

        // And it can be done afterwards.
        (await RestoreDirectAsync(page.Id, page.First, mark, _owner.UserId)).Answer.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_edit_stored_between_the_preview_and_the_commit_refuses_the_restore()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        var before = default(string);
        var edit = new Hooks(async at =>
        {
            if (at == RestoreStage.Prepared)
            {
                // Through the hub, as a person typing, so it waits for nothing the restore holds yet.
                await AppendAsync(page.Id, EditStreams.Get(1, 91));
                before = await SummaryAsync();
            }
        });

        var result = await RestoreDirectAsync(page.Id, page.First, mark, _owner.UserId, edit);

        (result.Status, result.Code).ShouldBe((StatusCodes.Status409Conflict, "page_changed"));
        (await SummaryAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_compaction_during_the_restore_does_not_stop_it_and_the_page_is_the_version()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        var compact = new Hooks(async at =>
        {
            if (at == RestoreStage.Prepared)
            {
                await using var scope = _factory.Services.CreateAsyncScope();
                (await scope.ServiceProvider.GetRequiredService<Compactor>().CompactAsync(page.Id, default)).ShouldBe(CompactionResult.Compacted);
            }
        });

        (await RestoreDirectAsync(page.Id, page.First, mark, _owner.UserId, compact)).Answer.ShouldNotBeNull();

        (await ContentAsync(page.Id)).ShouldBe(await VersionContentAsync(page.First));
    }

    [Fact]
    public async Task A_person_whose_role_was_lowered_meanwhile_is_refused_at_the_commit()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        var editor = await AddMemberAsync(WorkspaceRole.Editor);
        var lower = new Hooks(async at =>
        {
            if (at == RestoreStage.Prepared)
            {
                await UseDbAsync(db => db.WorkspaceMembers.Where(m => m.UserId == editor).ExecuteUpdateAsync(m => m.SetProperty(x => x.Role, WorkspaceRole.Viewer)));
            }
        });
        var before = await SummaryAsync();

        var result = await RestoreDirectAsync(page.Id, page.First, mark, editor, lower);

        (result.Status, result.Code).ShouldBe((StatusCodes.Status403Forbidden, "forbidden"));
        (await SummaryAsync()).ShouldBe(before);
    }

    // ---- the operation id -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_repeated_with_its_operation_id_gets_the_first_answer_and_restores_once_also_after_the_version_is_pruned()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        var operation = Guid.NewGuid();
        var first = await (await RestoreAsync(page.Id, page.First, mark, operation)).Content.ReadFromJsonAsync<RestoreAnswer>();
        var after = await SummaryAsync();

        var again = await RestoreAsync(page.Id, page.First, mark, operation);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<RestoreAnswer>()).ShouldBe(first);
        (await SummaryAsync()).ShouldBe(after);

        await UseDbAsync(db => db.PageVersions.Where(v => v.Id == page.First).ExecuteDeleteAsync());
        var pruned = await RestoreAsync(page.Id, page.First, mark, operation);
        pruned.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await pruned.Content.ReadFromJsonAsync<RestoreAnswer>()).ShouldBe(first);
    }

    [Fact]
    public async Task An_operation_id_used_for_another_restore_is_refused()
    {
        var page = await PageWithTwoVersionsAsync();
        var operation = Guid.NewGuid();
        (await RestoreAsync(page.Id, page.First, await MarkAsync(page.Id), operation)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await ExpectRefusedAsync(
            page.Id, async () => await RestoreAsync(page.Id, page.Second, await MarkAsync(page.Id), operation), HttpStatusCode.Conflict, "operation_mismatch");
    }

    // ---- who may restore --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_may_not_restore_a_stranger_learns_nothing_and_someone_signed_out_is_asked_to_sign_in()
    {
        var page = await PageWithTwoVersionsAsync();
        var mark = await MarkAsync(page.Id);
        var body = new { operationId = Guid.NewGuid(), expectedContent = mark.Content, expectedVersion = mark.Version };

        await AddMemberAsync(WorkspaceRole.Viewer, "viewer@example.com");
        var viewer = await SyncUser.LogInAsync(_factory, "viewer@example.com");
        var refused = await viewer.Http.PostJsonAsync(RestorePath(page.Id, page.First), body);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await AddUserAsync("stranger@example.com");
        var stranger = await SyncUser.LogInAsync(_factory, "stranger@example.com");
        (await stranger.Http.PostJsonAsync(RestorePath(page.Id, page.First), body)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // With the token an anonymous visitor is given, so that it is the sign-in that is missing.
        using var anonymous = _factory.CreateClient();
        (await anonymous.PostJsonAsync(RestorePath(page.Id, page.First), body)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Without the CSRF token, even the owner.
        (await _owner.Http.PostJsonWithoutTokenAsync(RestorePath(page.Id, page.First), body)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await VersionsAsync(page.Id)).ShouldAllBe(v => v.Kind != PageVersionKind.Restore);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private sealed record TestPage(Guid Id, Guid First, Guid Second);

    private sealed record Mark(string Content, int Version);

    private sealed class SimulatedCrash : Exception;

    private sealed class Hooks(Func<RestoreStage, Task> at) : IHistoryRestoreHooks
    {
        public Task AtAsync(RestoreStage stage, CancellationToken cancellationToken) => at(stage);
    }

    private IYjsCodec Codec => Services<IYjsCodec>();

    private T Services<T>()
        where T : notnull => _factory.Services.GetRequiredService<T>();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private static string RestorePath(Guid pageId, Guid versionId) => $"/api/pages/{pageId}/versions/{versionId}/restore";

    private async Task<Guid> CreatePageAsync(string title)
    {
        var created = await _owner.Http.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("page").GetProperty("id").GetGuid();
    }

    private async Task AppendAsync(Guid pageId, IEnumerable<byte[]> updates)
    {
        foreach (var update in updates)
        {
            await Services<PageUpdateStore>().AppendAsync(pageId, _owner.UserId, update, default);
        }
    }

    private Task<PageContent> StoredAsync(Guid pageId) => Services<PageUpdateStore>().LoadAsync(pageId, default);

    /// <summary>
    /// A page with two versions: the first under "First title" with some text, the second (a rename) under "Second title"
    /// with more text. The capture made both, as it would.
    /// </summary>
    private async Task<TestPage> PageWithTwoVersionsAsync()
    {
        var pageId = await CreatePageAsync("First title");
        var stream = EditStreams.Get(12, 5);
        await AppendAsync(pageId, stream.Take(6));
        (await Services<HistoryCapture>().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await AppendAsync(pageId, stream.Skip(6));
        (await _owner.Http.PatchJsonAsync($"/api/pages/{pageId}", new { title = "Second title" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Services<HistoryCapture>().CaptureAsync(pageId, default)).ShouldBe(HistoryCaptureOutcome.Captured);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var versions = await VersionsAsync(pageId);
        versions.Select(v => (v.Kind, v.Title)).ShouldBe([(PageVersionKind.Automatic, "First title"), (PageVersionKind.Rename, "Second title")]);
        return new TestPage(pageId, versions[0].Id, versions[1].Id);
    }

    private async Task<Mark> MarkAsync(Guid pageId)
    {
        var list = await _owner.Http.GetFromJsonAsync<VersionList>($"/api/pages/{pageId}/versions");
        var version = await UseDbAsync(db => db.Pages.Where(p => p.Id == pageId).Select(p => p.Version).SingleAsync());
        return new Mark(list!.PageContent, version);
    }

    private Task<HttpResponseMessage> RestoreAsync(Guid pageId, Guid versionId, Mark mark, Guid? operation = null) =>
        _owner.Http.PostJsonAsync(
            RestorePath(pageId, versionId),
            new { operationId = operation ?? Guid.NewGuid(), expectedContent = mark.Content, expectedVersion = mark.Version });

    /// <summary>The service itself, as the endpoint calls it, with hooks a test chooses and as any person.</summary>
    private async Task<RestoreResult> RestoreDirectAsync(Guid pageId, Guid versionId, Mark mark, Guid userId, IHistoryRestoreHooks? hooks = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<InkpactDbContext>();
        var restore = new HistoryRestore(
            db,
            services.GetRequiredService<IYjsCodec>(),
            services.GetRequiredService<HistorySnapshotBuilder>(),
            services.GetRequiredService<TreeService>(),
            services.GetRequiredService<IPermissionService>(),
            services.GetRequiredService<SyncHub>(),
            services.GetRequiredService<SyncOptions>(),
            _clock,
            hooks ?? new NoHistoryRestoreHooks(),
            NullLogger<HistoryRestore>.Instance);
        return await restore.RestoreAsync(pageId, versionId, userId, new RestoreRequest(Guid.NewGuid(), mark.Content, mark.Version), default);
    }

    private async Task ExpectRefusedAsync(Guid pageId, Func<Task<HttpResponseMessage>> restore, HttpStatusCode status, string code)
    {
        var before = await SummaryAsync();

        var response = await restore();

        response.StatusCode.ShouldBe(status);
        (await response.ReadErrorAsync()).Code.ShouldBe(code);
        (await SummaryAsync()).ShouldBe(before, $"the refused restore ({code}) wrote something");
    }

    private Task<List<PageVersion>> VersionsAsync(Guid pageId) => UseDbAsync(db =>
        db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToListAsync());

    /// <summary>The page as the editor shows it: its stored content, from the first paragraph on.</summary>
    private async Task<string> ContentAsync(Guid pageId)
    {
        var first = await Codec.BuildAsync(EmptyDocument, default);
        return Encoding.UTF8.GetString((await Codec.ReadAsync([first, .. (await StoredAsync(pageId)).Inputs], default)).Json);
    }

    private async Task<string> StateContentAsync(byte[] state) => Encoding.UTF8.GetString((await Codec.ReadAsync([state], default)).Json);

    private async Task<string> VersionContentAsync(Guid versionId) =>
        await StateContentAsync(await UseDbAsync(db => db.PageVersions.Where(v => v.Id == versionId).Select(v => v.State).SingleAsync()));

    private Task<Guid> AddVersionAsync(Guid pageId, byte[] state, string title, string fileText, DateTimeOffset? at = null) => UseDbAsync(async db =>
    {
        var version = new PageVersion(pageId, state, title, fileText, at ?? Start.AddMinutes(-1), null, PageVersionKind.Automatic);
        db.PageVersions.Add(version);
        await db.SaveChangesAsync();
        return version.Id;
    });

    private Task<Guid> AddUserAsync(string email) => UseDbAsync(async db =>
    {
        var user = new User { Email = email, DisplayName = email, PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = Start };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    });

    private async Task<Guid> AddMemberAsync(WorkspaceRole role, string? email = null)
    {
        var userId = await AddUserAsync(email ?? $"{Guid.NewGuid():N}@example.com");
        await UseDbAsync(async db =>
        {
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = userId, Role = role, CreatedAt = Start });
            return await db.SaveChangesAsync();
        });
        return userId;
    }

    /// <summary>Every row a restore writes, of every page: to compare before and after.</summary>
    private Task<string> SummaryAsync() => UseDbAsync(async db =>
    {
        var lines = new List<string>();
        lines.AddRange(await db.PageVersions.AsNoTracking().OrderBy(v => v.Id)
            .Select(v => $"version {v.Id} {v.PageId} {v.Kind} {v.Title} {v.ProtectedUntil} {v.PayloadBytes} {v.CreatedAt.UtcTicks}").ToListAsync());
        lines.AddRange(await db.PageUpdates.AsNoTracking().OrderBy(u => u.Seq).Select(u => $"update {u.Seq} {u.PageId}").ToListAsync());
        lines.AddRange(await db.PageSnapshots.AsNoTracking().OrderBy(s => s.PageId)
            .Select(s => $"snapshot {s.PageId} {s.Watermark} {s.UpdateCount} {s.Version}").ToListAsync());
        lines.AddRange(await db.Pages.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => $"page {p.Id} {p.Title} {p.Version} {p.UpdatedAt.UtcTicks} {p.ContentText} {p.FileText} {p.LinksIndexed}").ToListAsync());
        lines.AddRange(await db.PageLinks.AsNoTracking().OrderBy(l => l.SourcePageId).ThenBy(l => l.TargetPageId)
            .Select(l => $"link {l.SourcePageId} {l.TargetPageId}").ToListAsync());
        lines.AddRange(await db.PageHistoryStates.AsNoTracking().OrderBy(s => s.PageId)
            .Select(s => $"history {s.PageId} {s.CapturedSeq} {s.CapturedUpdates} {s.Failure}").ToListAsync());
        lines.AddRange(await db.PageRestores.AsNoTracking().OrderBy(r => r.OperationId).Select(r => $"restore {r.OperationId}").ToListAsync());
        lines.AddRange(await db.PageTitleChanges.AsNoTracking().OrderBy(c => c.Id).Select(c => $"title {c.Id} {c.Title}").ToListAsync());
        lines.Add($"tree {await db.Workspaces.Select(w => w.TreeVersion).SingleAsync()}");
        return string.Join("\n", lines);
    });
}
