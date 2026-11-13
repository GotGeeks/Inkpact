using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Crdt;
using Inkpact.Api.History;
using Inkpact.Api.Permissions;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.History;

/// <summary>
/// Reading a page's history over HTTP (run B.5b): the list with its paging and the page's history state, one version's
/// content, and who may read them. With the real sidecar, which reads a version's content.
/// </summary>
public class HistoryEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Document =
        """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"What the version says"}]}]}"""u8.ToArray();

    private InkpactFactory _factory = null!;
    private HttpClient _owner = null!;
    private Guid _workspaceId;
    private Guid _ownerId;
    private Guid _pageId;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: new MutableTimeProvider(Now), sidecar: true);
        _owner = await _factory.SignedInClientAsync();
        await _factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        (_workspaceId, _ownerId) = await UseDbAsync(async db =>
        {
            var workspace = await db.Workspaces.SingleAsync();
            return (workspace.Id, workspace.OwnerId);
        });
        _pageId = await AddPageAsync(_workspaceId);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ---- the list ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_is_newest_first_with_what_a_row_shows_and_without_the_content()
    {
        await AddVersionsAsync(
            // Its protection ended just now (the safety version of an earlier restore): no longer said.
            new PageVersion(_pageId, new byte[10], "First", "", Now.AddHours(-3), null, PageVersionKind.Automatic, protectedUntil: Now),
            new PageVersion(_pageId, new byte[20], "Renamed", "", Now.AddHours(-2), _ownerId, PageVersionKind.Rename),
            new PageVersion(_pageId, new byte[30], "Renamed", "four", Now.AddHours(-1), _ownerId, PageVersionKind.BeforeRestore, protectedUntil: Now.AddHours(23)),
            new PageVersion(_pageId, new byte[40], "First", "", Now, _ownerId, PageVersionKind.Restore));

        var response = await _owner.GetAsync(ListPath(_pageId));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("\"state\":[");
        body.ShouldNotContain("\"content\"", Case.Insensitive);
        var list = JsonSerializer.Deserialize<VersionList>(body, JsonSerializerOptions.Web).ShouldNotBeNull();
        list.Versions.Select(v => (v.Kind, v.Title, v.Bytes)).ShouldBe(
        [
            ("restore", "First", 40),
            ("before_restore", "Renamed", 34),
            ("rename", "Renamed", 20),
            ("automatic", "First", 10),
        ]);
        list.Versions[0].CreatedAt.ShouldBe(Now);
        // Only the version before a restore is protected, and says until when; a protection that has passed is left out.
        list.Versions.Select(v => v.ProtectedUntil).ShouldBe([null, Now.AddHours(23), null, null]);
        // Who it was, when it was one person; nobody for a version that can hold several people's edits.
        var ownerName = await UseDbAsync(db => db.Users.Where(u => u.Id == _ownerId).Select(u => u.DisplayName).SingleAsync());
        list.Versions[2].CreatedBy.ShouldBe(new VersionAuthor(_ownerId, ownerName));
        list.Versions[3].CreatedBy.ShouldBeNull();
        list.Next.ShouldBeNull();
        // The page's content as stored, for a restore to send back: nothing stored yet.
        list.PageContent.ShouldBe(ContentMark.Of(0, 0));
    }

    [Fact]
    public async Task The_list_gives_the_page_s_content_mark_which_counts_updates_in_the_snapshot_and_live()
    {
        await UseDbAsync(async db =>
        {
            db.PageSnapshots.Add(new PageSnapshot { PageId = _pageId, State = [1], StateVector = [1], Watermark = 7, Version = 1, UpdateCount = 4, UpdatedAt = Now });
            db.PageUpdates.Add(new PageUpdate { PageId = _pageId, UserId = _ownerId, Data = [1], CreatedAt = Now });
            return await db.SaveChangesAsync();
        });
        var seq = await UseDbAsync(db => db.PageUpdates.Where(u => u.PageId == _pageId).Select(u => u.Seq).SingleAsync());

        (await ListAsync(_pageId)).PageContent.ShouldBe(ContentMark.Of(Math.Max(7, seq), 5));
    }

    [Theory]
    [InlineData("3.4", true, 3, 4)]
    [InlineData("0.0", true, 0, 0)]
    [InlineData("3", false, 0, 0)]
    [InlineData("3.4.5", false, 0, 0)]
    [InlineData("-3.4", false, 0, 0)]
    [InlineData(" 3.4", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    public void A_content_mark_reads_back_only_as_two_whole_numbers(string? mark, bool ok, long seq, long count)
    {
        ContentMark.TryParse(mark, out var readSeq, out var readCount).ShouldBe(ok);
        if (ok)
        {
            (readSeq, readCount).ShouldBe((seq, count));
        }
    }

    [Fact]
    public async Task The_list_is_read_in_pages_that_neither_repeat_nor_miss_a_version_made_at_the_same_time()
    {
        // Five at one time, between an older and a newer one: the cursor has to tell them apart by their id.
        var versions = Enumerable.Range(0, 5)
            .Select(i => new PageVersion(_pageId, new byte[1], $"Same time {i}", "", Now.AddMinutes(-5), null, PageVersionKind.Automatic))
            .Append(new PageVersion(_pageId, new byte[1], "Oldest", "", Now.AddMinutes(-9), null, PageVersionKind.Automatic))
            .Append(new PageVersion(_pageId, new byte[1], "Newest", "", Now, null, PageVersionKind.Automatic))
            .ToArray();
        await AddVersionsAsync(versions);

        var seen = new List<VersionSummary>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var list = await ListAsync(_pageId, cursor is null ? "?limit=2" : $"?limit=2&before={cursor}");
            list.Versions.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(list.Versions);
            cursor = list.Next;
            (++pages).ShouldBeLessThan(10);
        }
        while (cursor is not null);

        pages.ShouldBe(4);
        seen.Select(v => v.Id).Order().ShouldBe(versions.Select(v => v.Id).Order());
        seen[0].Title.ShouldBe("Newest");
        seen[^1].Title.ShouldBe("Oldest");
        seen.Select(v => v.CreatedAt).ShouldBe(seen.Select(v => v.CreatedAt).OrderDescending());
    }

    [Theory]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-3", 1)]
    [InlineData("?limit=1000", HistoryEndpoints.MaxPageSize)]
    [InlineData("", HistoryEndpoints.DefaultPageSize)]
    public async Task The_size_of_a_page_stays_within_its_bounds(string query, int expected)
    {
        await AddVersionsAsync([.. Enumerable.Range(0, HistoryEndpoints.MaxPageSize + 5)
            .Select(i => new PageVersion(_pageId, [], "Page", "", Now.AddMinutes(-i), null, PageVersionKind.Automatic))]);

        var list = await ListAsync(_pageId, query);

        list.Versions.Count.ShouldBe(expected);
        list.Next.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("12")]
    [InlineData("12_34")]
    [InlineData("-1_00000000000000000000000000000000")]
    [InlineData("99999999999999999999_00000000000000000000000000000000")]
    [InlineData("1_2_3")]
    public async Task A_cursor_that_is_not_one_is_refused(string cursor)
    {
        var response = await _owner.GetAsync($"{ListPath(_pageId)}?before={cursor}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("cursor_invalid");
    }

    [Fact]
    public void A_cursor_reads_back_as_the_time_and_id_it_was_written_from()
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 12, 9, 30, 15, TimeSpan.Zero).AddTicks(1230);

        HistoryEndpoints.TryReadCursor(HistoryEndpoints.WriteCursor(at, id), out var readAt, out var readId).ShouldBeTrue();

        (readAt, readId).ShouldBe((at, id));
    }

    // ---- the page's history state -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_says_where_the_page_s_history_stands()
    {
        // Nothing stored, nothing waiting.
        var fresh = await ListAsync(_pageId);
        fresh.Versions.ShouldBeEmpty();
        (fresh.Next, fresh.State, fresh.Failure).ShouldBe((null, HistoryStates.Ready, null));

        // Something stored that is in no version yet.
        await UseDbAsync(async db =>
        {
            db.PageUpdates.Add(new PageUpdate { PageId = _pageId, UserId = _ownerId, Data = [1], CreatedAt = Now });
            return await db.SaveChangesAsync();
        });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Pending);

        // The capture saw it.
        await SetStateAsync(new PageHistoryState { PageId = _pageId, CapturedSeq = 1, CapturedUpdates = 1 });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Ready);

        // A title is waiting for its version.
        await UseDbAsync(async db =>
        {
            db.PageTitleChanges.Add(new PageTitleChange { PageId = _pageId, Title = "Renamed", PageVersion = 2, ChangedAt = Now });
            return await db.SaveChangesAsync();
        });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Pending);

        // The page cannot be read; the cap is reached; the sidecar was down, which is not the page's failure.
        await SetStateAsync(new PageHistoryState { PageId = _pageId, FailedAttempts = 1, NextAttemptAt = Now.AddHours(1), Failure = PageHistoryFailures.Unreadable });
        var failed = await ListAsync(_pageId);
        (failed.State, failed.Failure).ShouldBe((HistoryStates.Failed, PageHistoryFailures.Unreadable));
        await SetStateAsync(new PageHistoryState { PageId = _pageId, FailedAttempts = 1, NextAttemptAt = Now.AddHours(1), Failure = PageHistoryFailures.Full });
        var full = await ListAsync(_pageId);
        (full.State, full.Failure).ShouldBe((HistoryStates.Full, null));
        await SetStateAsync(new PageHistoryState { PageId = _pageId, FailedAttempts = 3, NextAttemptAt = Now.AddMinutes(4) });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Pending);

        // History off says so, whatever else is the case, and the versions there are stay readable.
        await AddVersionsAsync(new PageVersion(_pageId, new byte[1], "Kept", "", Now, null, PageVersionKind.Automatic));
        await UseDbAsync(db => db.Workspaces.Where(w => w.Id == _workspaceId).ExecuteUpdateAsync(w => w.SetProperty(x => x.HistoryEnabled, false)));
        var off = await ListAsync(_pageId);
        (off.State, off.Failure).ShouldBe((HistoryStates.Off, null));
        off.Versions.ShouldHaveSingleItem().Title.ShouldBe("Kept");
    }

    [Fact]
    public async Task A_page_whose_updates_are_all_in_its_snapshot_is_pending_until_they_are_captured()
    {
        await UseDbAsync(async db =>
        {
            db.PageSnapshots.Add(new PageSnapshot { PageId = _pageId, State = [1], StateVector = [1], Watermark = 7, Version = 1, UpdateCount = 4, UpdatedAt = Now });
            return await db.SaveChangesAsync();
        });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Pending);

        await SetStateAsync(new PageHistoryState { PageId = _pageId, CapturedSeq = 7, CapturedUpdates = 4 });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Ready);
    }

    // ---- one version ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_version_is_read_as_its_title_and_the_document_the_editor_shows()
    {
        var state = await _factory.Services.GetRequiredService<IYjsCodec>().BuildAsync(Document, default);
        var version = new PageVersion(_pageId, state, "The title then", "", Now.AddDays(-1), null, PageVersionKind.Automatic);
        await AddVersionsAsync(version);

        var response = await _owner.GetAsync(VersionPath(_pageId, version.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = (await response.Content.ReadFromJsonAsync<VersionDetail>()).ShouldNotBeNull();
        (detail.Id, detail.CreatedAt, detail.Kind, detail.Title).ShouldBe((version.Id, Now.AddDays(-1), "automatic", "The title then"));
        detail.Content.GetProperty("type").GetString().ShouldBe("doc");
        var paragraph = detail.Content.GetProperty("content").EnumerateArray().ShouldHaveSingleItem();
        paragraph.GetProperty("type").GetString().ShouldBe("paragraph");
        paragraph.GetProperty("content")[0].GetProperty("text").GetString().ShouldBe("What the version says");
    }

    [Fact]
    public async Task One_version_is_read_at_a_time_and_a_request_that_waits_too_long_is_told_to_come_back()
    {
        await using var factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(),
            sidecar: true,
            settings: new Dictionary<string, string> { ["History:PreviewWait"] = "00:00:00.200" });
        using var owner = await factory.SignedInClientAsync();
        await factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        var state = await factory.Services.GetRequiredService<IYjsCodec>().BuildAsync(Document, default);
        Guid pageId;
        PageVersion version;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var page = NewPage(await db.Workspaces.Select(w => w.Id).SingleAsync());
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            pageId = page.Id;
            version = new PageVersion(pageId, state, "Page", "", Now, null, PageVersionKind.Automatic);
            db.PageVersions.Add(version);
            await db.SaveChangesAsync();
        }

        var gate = factory.Services.GetRequiredService<HistoryPreviewGate>();
        using (await gate.EnterAsync(default))
        {
            var response = await owner.GetAsync(VersionPath(pageId, version.Id));
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await response.ReadErrorAsync()).Code.ShouldBe("history_unavailable");
            // A version that is not there is said to be not there without waiting for a turn, and the list needs none.
            (await owner.GetAsync(VersionPath(pageId, Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await owner.GetAsync(ListPath(pageId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // The turn is given back, also by a read that failed.
        var read = await owner.GetAsync(VersionPath(pageId, version.Id));
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        // As every answer of the API: not kept by the browser.
        read.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await owner.GetAsync(VersionPath(pageId, version.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_changed_policy_lets_a_page_that_was_full_try_again_and_leaves_other_failures_as_they_are()
    {
        var unreadable = await AddPageAsync(_workspaceId);
        await SetStateAsync(new PageHistoryState { PageId = _pageId, FailedAttempts = 2, NextAttemptAt = Now.AddHours(1), Failure = PageHistoryFailures.Full, FailedAt = Now });
        await SetStateAsync(new PageHistoryState { PageId = unreadable, FailedAttempts = 2, NextAttemptAt = Now.AddHours(2), Failure = PageHistoryFailures.Unreadable, FailedAt = Now });
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Full);

        var message = new HttpRequestMessage(HttpMethod.Put, $"/api/workspaces/{_workspaceId}/history-policy")
        {
            Content = JsonContent.Create(new HistoryPolicyDto(true, 24, 30, 365, 64)),
        };
        message.Headers.Add(CsrfProtection.HeaderName, (await _owner.GetStateAsync()).CsrfToken);
        (await _owner.SendAsync(message)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Ready);
        var cleared = await UseDbAsync(db => db.PageHistoryStates.AsNoTracking().SingleAsync(s => s.PageId == _pageId));
        (cleared.Failure, cleared.FailedAt, cleared.NextAttemptAt, cleared.FailedAttempts).ShouldBe((null, null, null, 0));
        var kept = await UseDbAsync(db => db.PageHistoryStates.AsNoTracking().SingleAsync(s => s.PageId == unreadable));
        (kept.Failure, kept.NextAttemptAt, kept.FailedAttempts).ShouldBe((PageHistoryFailures.Unreadable, Now.AddHours(2), 2));
    }

    [Fact]
    public async Task A_version_is_only_found_within_its_own_page()
    {
        var otherPage = await AddPageAsync(_workspaceId);
        var mine = new PageVersion(_pageId, new byte[1], "Mine", "", Now, null, PageVersionKind.Automatic);
        var theirs = new PageVersion(otherPage, new byte[1], "Theirs", "", Now, null, PageVersionKind.Automatic);
        await AddVersionsAsync(mine, theirs);

        // Both pages are the owner's, and still the id of one page's version says nothing under the other.
        foreach (var (pageId, versionId) in new[] { (_pageId, theirs.Id), (otherPage, mine.Id), (_pageId, Guid.NewGuid()) })
        {
            var response = await _owner.GetAsync(VersionPath(pageId, versionId));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.ReadErrorAsync()).Code.ShouldBe("version_not_found");
        }

        (await ListAsync(_pageId)).Versions.ShouldHaveSingleItem().Id.ShouldBe(mine.Id);
        (await ListAsync(otherPage)).Versions.ShouldHaveSingleItem().Id.ShouldBe(theirs.Id);
    }

    [Fact]
    public async Task A_version_that_cannot_be_read_back_whole_is_not_shown_in_part()
    {
        // Not a Yjs state at all.
        var broken = new PageVersion(_pageId, [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f], "Broken", "", Now, null, PageVersionKind.Automatic);
        await AddVersionsAsync(broken);

        var response = await _owner.GetAsync(VersionPath(_pageId, broken.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadErrorAsync()).Code.ShouldBe("version_unreadable");
        // The list still shows it: that it exists is true.
        (await ListAsync(_pageId)).Versions.ShouldHaveSingleItem().Id.ShouldBe(broken.Id);
    }

    [Fact]
    public async Task Without_the_sidecar_the_list_still_answers_and_a_version_says_history_is_unavailable()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: new MutableTimeProvider(Now));
        using var owner = await factory.SignedInClientAsync();
        Guid pageId;
        var version = default(PageVersion)!;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var page = NewPage(await db.Workspaces.Select(w => w.Id).SingleAsync());
            db.Pages.Add(page);
            await db.SaveChangesAsync();
            pageId = page.Id;
            version = new PageVersion(pageId, new byte[1], "Page", "", Now, null, PageVersionKind.Automatic);
            db.PageVersions.Add(version);
            await db.SaveChangesAsync();
        }

        (await owner.GetAsync(ListPath(pageId))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await owner.GetAsync(VersionPath(pageId, version.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.ReadErrorAsync()).Code.ShouldBe("history_unavailable");
    }

    // ---- who may read -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Every_member_of_the_workspace_can_read_a_page_s_history(WorkspaceRole role)
    {
        var (pageId, versionId) = await OtherWorkspaceAsync(role);

        (await ListAsync(pageId)).Versions.ShouldHaveSingleItem().Id.ShouldBe(versionId);
        (await _owner.GetAsync(VersionPath(pageId, versionId))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_stranger_learns_nothing_of_a_page_s_history_nor_that_the_page_exists()
    {
        var (pageId, versionId) = await OtherWorkspaceAsync(null);

        foreach (var path in new[] { ListPath(pageId), VersionPath(pageId, versionId), ListPath(Guid.NewGuid()), VersionPath(Guid.NewGuid(), versionId) })
        {
            var response = await _owner.GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
            (await response.ReadErrorAsync()).Code.ShouldBe("not_found");
        }
    }

    [Fact]
    public async Task Someone_who_is_not_signed_in_is_asked_to()
    {
        using var anonymous = _factory.CreateClient();

        (await anonymous.GetAsync(ListPath(_pageId))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(VersionPath(_pageId, Guid.NewGuid()))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_history_of_a_page_in_the_trash_can_still_be_looked_at()
    {
        var version = new PageVersion(_pageId, await _factory.Services.GetRequiredService<IYjsCodec>().BuildAsync(Document, default), "Page", "", Now, null, PageVersionKind.Automatic);
        await AddVersionsAsync(version);
        await UseDbAsync(db => db.Pages.Where(p => p.Id == _pageId).ExecuteUpdateAsync(p => p.SetProperty(x => x.DeletedAt, Now)));

        (await ListAsync(_pageId)).Versions.ShouldHaveSingleItem();
        (await _owner.GetAsync(VersionPath(_pageId, version.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- a rename leaves its title for the capture ------------------------------------------------------------------------

    [Fact]
    public async Task A_rename_through_the_api_leaves_the_new_title_and_who_gave_it_for_the_capture()
    {
        (await _owner.PatchJsonAsync($"/api/pages/{_pageId}", new { title = "A new title" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        // The same title again changes nothing, so it leaves nothing.
        (await _owner.PatchJsonAsync($"/api/pages/{_pageId}", new { title = "A new title" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var change = (await UseDbAsync(db => db.PageTitleChanges.AsNoTracking().ToListAsync())).ShouldHaveSingleItem();
        (change.PageId, change.Title, change.ChangedBy, change.ChangedAt).ShouldBe((_pageId, "A new title", _ownerId, Now));
        change.PageVersion.ShouldBe(await UseDbAsync(db => db.Pages.Where(p => p.Id == _pageId).Select(p => p.Version).SingleAsync()) - 1);
        (await ListAsync(_pageId)).State.ShouldBe(HistoryStates.Pending);
    }

    [Fact]
    public async Task A_page_the_capture_only_put_off_and_one_it_still_cannot_read_are_told_apart()
    {
        // Put off because the sidecar was down, with what was known of the page kept: it is still one that cannot be read.
        await SetStateAsync(new PageHistoryState
        {
            PageId = _pageId,
            FailedAttempts = 1,
            WaitingAttempts = 2,
            NextAttemptAt = Now.AddHours(1),
            Failure = PageHistoryFailures.Unreadable,
            FailedAt = Now,
        });
        var list = await ListAsync(_pageId);
        (list.State, list.Failure).ShouldBe((HistoryStates.Failed, PageHistoryFailures.Unreadable));
    }

    // ---- the worker, through the app as it runs ---------------------------------------------------------------------------

    [Fact]
    public async Task A_page_edited_over_the_socket_and_renamed_gets_its_versions_from_the_worker()
    {
        await using var factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(),
            sidecar: true,
            settings: new Dictionary<string, string>
            {
                ["History:CaptureEnabled"] = "True",
                ["History:CaptureInterval"] = "00:00:00.200",
                // Idle at once, so the test does not wait two minutes for it.
                ["History:CaptureIdleAfter"] = "00:00:00",
            });
        var user = await SyncUser.SetUpAdministratorAsync(factory);
        await factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        Guid workspaceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            workspaceId = await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Workspaces.Select(w => w.Id).SingleAsync();
        }

        var created = await user.Http.PostJsonAsync($"/api/workspaces/{workspaceId}/pages", new { title = "Written here" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pageId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("page").GetProperty("id").GetGuid();
        var edits = EditStreams.Get(3, 1);
        await using (var client = await user.ConnectAsync())
        {
            await client.JoinAsync(pageId);
            foreach (var edit in edits)
            {
                await client.SendUpdateAsync(pageId, edit);
            }
        }

        // Everything that was sent is stored and in a version: nothing is waiting.
        var list = await UntilAsync(
            () => user.Http.GetFromJsonAsync<VersionList>(ListPath(pageId))!,
            l => l!.State == HistoryStates.Ready && l.Versions.Count > 0 && StoredAsync(factory, pageId).GetAwaiter().GetResult() == edits.Count);
        list!.Versions.ShouldAllBe(v => v.Kind == "automatic" && v.Title == "Written here" && v.CreatedBy == null);
        var newest = await user.Http.GetFromJsonAsync<VersionDetail>(VersionPath(pageId, list.Versions[0].Id));
        newest!.Content.GetProperty("type").GetString().ShouldBe("doc");

        // The newest version is the page as stored.
        var codec = factory.Services.GetRequiredService<IYjsCodec>();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var state = await db.PageVersions.Where(v => v.Id == list.Versions[0].Id).Select(v => v.State).SingleAsync();
            var stored = await db.PageUpdates.Where(u => u.PageId == pageId).OrderBy(u => u.Seq).Select(u => u.Data).ToListAsync();
            (await codec.FingerprintAsync([state], default))
                .ShouldBe(await codec.FingerprintAsync([await codec.BuildAsync("""{"type":"doc"}"""u8.ToArray(), default), .. stored], default));
        }

        (await user.Http.PatchJsonAsync($"/api/pages/{pageId}", new { title = "Renamed here" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var renamed = await UntilAsync(
            () => user.Http.GetFromJsonAsync<VersionList>(ListPath(pageId))!,
            l => l!.State == HistoryStates.Ready && l.Versions[0].Kind == "rename");
        renamed!.Versions[0].Title.ShouldBe("Renamed here");
        renamed.Versions[0].CreatedBy!.Id.ShouldBe(user.UserId);
    }

    private static async Task<int> StoredAsync(InkpactFactory factory, Guid pageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().PageUpdates.CountAsync(u => u.PageId == pageId);
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(60), "the worker did not get there");
            await Task.Delay(100);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private static string ListPath(Guid pageId) => $"/api/pages/{pageId}/versions";

    private static string VersionPath(Guid pageId, Guid versionId) => $"/api/pages/{pageId}/versions/{versionId}";

    private async Task<VersionList> ListAsync(Guid pageId, string query = "")
    {
        var response = await _owner.GetAsync(ListPath(pageId) + query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<VersionList>()).ShouldNotBeNull();
    }

    private static Page NewPage(Guid workspaceId) => new()
    {
        WorkspaceId = workspaceId,
        Title = "Page",
        SortKey = $"a{Guid.NewGuid():N}",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private Task<Guid> AddPageAsync(Guid workspaceId) => UseDbAsync(async db =>
    {
        var page = NewPage(workspaceId);
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return page.Id;
    });

    private Task AddVersionsAsync(params PageVersion[] versions) => UseDbAsync(async db =>
    {
        db.PageVersions.AddRange(versions);
        return await db.SaveChangesAsync();
    });

    private Task SetStateAsync(PageHistoryState state) => UseDbAsync(async db =>
    {
        await db.PageHistoryStates.Where(s => s.PageId == state.PageId).ExecuteDeleteAsync();
        db.PageHistoryStates.Add(state);
        return await db.SaveChangesAsync();
    });

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    /// <summary>Another person's workspace with a page and a version, in which the signed-in person has a role, or none.</summary>
    private Task<(Guid PageId, Guid VersionId)> OtherWorkspaceAsync(WorkspaceRole? role) => UseDbAsync(async db =>
    {
        var other = new User { Email = "other@example.com", DisplayName = "Other", CreatedAt = Now };
        var workspace = new Workspace { Name = "Other", OwnerId = other.Id, CreatedAt = Now };
        db.AddRange(other, workspace);
        if (role != WorkspaceRole.Owner)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = other.Id, Role = WorkspaceRole.Owner, CreatedAt = Now });
        }

        if (role is { } memberRole)
        {
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = _ownerId, Role = memberRole, CreatedAt = Now });
        }

        var page = NewPage(workspace.Id);
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        var state = await _factory.Services.GetRequiredService<IYjsCodec>().BuildAsync(Document, default);
        var version = new PageVersion(page.Id, state, "Page", "", Now, null, PageVersionKind.Automatic);
        db.PageVersions.Add(version);
        await db.SaveChangesAsync();
        return (page.Id, version.Id);
    });
}
