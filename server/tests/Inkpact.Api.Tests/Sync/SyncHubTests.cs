using System.Net;
using System.Net.WebSockets;
using Inkpact.Api.Auth;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Inkpact.Api.Tests.Testing.SyncTestClient;

namespace Inkpact.Api.Tests.Sync;

/// <summary>
/// The sync hub through real sockets on the test server, against Postgres. The updates are opaque bytes here, as they
/// are to the hub, so the Yjs sidecar is off and every subscribe is answered with the stored log (log mode); the diff
/// is tested with real updates in SyncHubDiffTests, and what Yjs makes of them in the browser with the editor (run 1.11).
/// </summary>
[Collection(PostgresCollection.Name)]
public class SyncHubTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = "";
    private InkpactFactory _factory = null!;
    private SyncUser _jos = null!;
    private Guid _workspaceId;
    private readonly List<IAsyncDisposable> _sockets = [];

    public Task InitializeAsync() => StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            await socket.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    /// <summary>Starts the app on a fresh database, with the limits a test wants, and sets up the administrator.</summary>
    private async Task StartAsync(Dictionary<string, string>? settings = null)
    {
        _factory?.Dispose();
        _connectionString = await postgres.CreateDatabaseAsync();
        _factory = new InkpactFactory(_connectionString, settings: settings);
        _jos = await SyncUser.SetUpAdministratorAsync(_factory);
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private SyncHub Hub => _factory.Services.GetRequiredService<SyncHub>();

    private async Task<SyncTestClient> ConnectAsync(SyncUser? user = null)
    {
        var client = await (user ?? _jos).ConnectAsync();
        _sockets.Add(client);
        return client;
    }

    private async Task<Guid> CreatePageAsync(string title = "Page")
    {
        var response = await _jos.Http.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PageChange>())!.Page.Id;
    }

    /// <summary>Someone else, with this role in Jos's workspace, or none (then they have a workspace of their own).</summary>
    private async Task<SyncUser> OtherPersonAsync(WorkspaceRole? role)
    {
        var email = $"other-{Guid.NewGuid():N}@example.com";
        await UseDbAsync(async db =>
        {
            var user = new User { Email = email, DisplayName = "Other", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = DateTimeOffset.UtcNow };
            db.Users.Add(user);
            if (role is { } given)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = _workspaceId, UserId = user.Id, Role = given, CreatedAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync();
            return 0;
        });
        return await SyncUser.LogInAsync(_factory, email);
    }

    private Task<List<PageUpdate>> StoredAsync(Guid pageId) =>
        UseDbAsync(db => db.PageUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Seq).ToListAsync());

    private static async Task HandshakeFailsWithAsync(Func<Task> connect, HttpStatusCode status)
    {
        var error = await Should.ThrowAsync<InvalidOperationException>(connect);
        error.Message.ShouldContain(((int)status).ToString());
    }

    // ---- who may connect -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_visitor_who_is_not_signed_in_cannot_open_a_socket()
    {
        var socketClient = _factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request => request.Headers.Origin = "http://localhost";

        await HandshakeFailsWithAsync(
            () => socketClient.ConnectAsync(new Uri("ws://localhost/api/sync"), CancellationToken.None), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://localhost")]
    [InlineData("null")]
    public async Task A_socket_from_another_origin_is_refused(string origin)
    {
        await HandshakeFailsWithAsync(() => _jos.ConnectAsync(origin), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_socket_without_an_origin_is_refused()
    {
        await HandshakeFailsWithAsync(() => _jos.ConnectAsync(origin: null), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_plain_request_to_the_sync_path_is_told_to_use_a_websocket()
    {
        var response = await _jos.Http.GetAsync(SyncEndpoints.Path);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("websocket_required");
    }

    [Fact]
    public async Task A_person_may_not_open_more_sockets_than_the_limit_and_a_closed_one_frees_its_slot()
    {
        await StartAsync(new() { ["Sync:MaxConnectionsPerUser"] = "2" });
        var first = await ConnectAsync();
        await ConnectAsync();

        await HandshakeFailsWithAsync(() => _jos.ConnectAsync(), HttpStatusCode.TooManyRequests);

        await first.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await WaitUntilAsync(() => Hub.OpenConnections == 1);
        await ConnectAsync();
    }

    // ---- which pages a socket may receive -------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_that_does_not_exist_is_not_found()
    {
        var client = await ConnectAsync();
        var missing = Guid.CreateVersion7();

        await client.SubscribeAsync(missing);

        var answer = await client.ReceiveAsync();
        answer.Type.ShouldBe(MessageType.Error);
        answer.PageId.ShouldBe(missing);
        answer.Code.ShouldBe(SyncErrors.PageNotFound);
    }

    [Fact]
    public async Task A_page_in_someone_elses_workspace_is_not_found_and_its_updates_are_not_sent()
    {
        var page = await CreatePageAsync();
        var jos = await ConnectAsync();
        await jos.JoinAsync(page);
        await jos.SendUpdateAsync(page, Bytes("secret"));
        var stranger = await ConnectAsync(await OtherPersonAsync(role: null));

        await stranger.SubscribeAsync(page);

        var answer = await stranger.ReceiveAsync();
        answer.Code.ShouldBe(SyncErrors.PageNotFound);
        await jos.SendUpdateAsync(page, Bytes("more secret"));
        await stranger.ExpectSilenceAsync();
    }

    [Fact]
    public async Task Sending_to_a_page_the_socket_does_not_receive_closes_it()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();

        await client.SendUpdateAsync(page, Bytes("unasked"));

        var close = await client.ExpectCloseAsync();
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        close.CloseReason.ShouldBe(SyncClose.ProtocolErrorReason);
        (await StoredAsync(page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_viewer_receives_the_page_but_their_updates_are_refused_and_not_stored()
    {
        var page = await CreatePageAsync();
        var jos = await ConnectAsync();
        await jos.JoinAsync(page);
        await jos.SendUpdateAsync(page, Bytes("by jos"));
        var viewer = await ConnectAsync(await OtherPersonAsync(WorkspaceRole.Viewer));

        (await viewer.JoinAsync(page)).Select(u => u.ToArray()).ShouldBe([Bytes("by jos")]);
        await viewer.SendUpdateAsync(page, Bytes("by the viewer"));

        var refusal = await viewer.ReceiveAsync();
        refusal.Type.ShouldBe(MessageType.Error);
        refusal.Code.ShouldBe(SyncErrors.ReadOnly);
        await jos.ExpectSilenceAsync();
        (await StoredAsync(page)).Select(u => u.Data).ShouldBe([Bytes("by jos")]);
    }

    [Fact]
    public async Task An_editor_who_is_not_the_owner_may_change_the_page()
    {
        var page = await CreatePageAsync();
        var editor = await OtherPersonAsync(WorkspaceRole.Editor);
        var client = await ConnectAsync(editor);
        await client.JoinAsync(page);

        await client.SendUpdateAsync(page, Bytes("by the editor"));
        await client.ExpectSilenceAsync();

        var stored = (await StoredAsync(page)).ShouldHaveSingleItem();
        stored.UserId.ShouldBe(editor.UserId);
    }

    [Fact]
    public async Task A_page_in_the_trash_can_still_be_edited()
    {
        var page = await CreatePageAsync();
        (await _jos.Http.PostJsonAsync($"/api/pages/{page}/trash")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var client = await ConnectAsync();
        await client.JoinAsync(page);

        await client.SendUpdateAsync(page, Bytes("kept"));
        await client.ExpectSilenceAsync();

        (await StoredAsync(page)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_socket_may_receive_only_so_many_pages()
    {
        await StartAsync(new() { ["Sync:MaxPagesPerConnection"] = "2" });
        var pages = new[] { await CreatePageAsync("1"), await CreatePageAsync("2"), await CreatePageAsync("3") };
        var client = await ConnectAsync();
        await client.JoinAsync(pages[0]);
        await client.JoinAsync(pages[1]);

        await client.SubscribeAsync(pages[2]);

        (await client.ReceiveAsync()).Code.ShouldBe(SyncErrors.TooManyPages);
        await client.UnsubscribeAsync(pages[0]);
        await client.JoinAsync(pages[2]);
    }

    // ---- storing and relaying (the spike's scenarios) -------------------------------------------------------------------

    [Fact]
    public async Task An_update_is_stored_once_with_its_author_and_relayed_to_the_others_but_not_back()
    {
        var page = await CreatePageAsync();
        var first = await ConnectAsync();
        var second = await ConnectAsync(await _jos.LogInAgainAsync());
        (await first.JoinAsync(page)).ShouldBeEmpty();
        (await second.JoinAsync(page)).ShouldBeEmpty();

        await first.SendUpdateAsync(page, Bytes("hello"));

        var relayed = await second.ReceiveAsync();
        relayed.Type.ShouldBe(MessageType.Update);
        relayed.PageId.ShouldBe(page);
        relayed.Payload.ShouldBe(Bytes("hello"));
        await first.ExpectSilenceAsync();
        var stored = (await StoredAsync(page)).ShouldHaveSingleItem();
        stored.Data.ShouldBe(Bytes("hello"));
        stored.UserId.ShouldBe(_jos.UserId);
    }

    [Fact]
    public async Task A_late_joiner_gets_every_stored_update_in_order()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);
        for (var i = 0; i < 20; i++)
        {
            await writer.SendUpdateAsync(page, Bytes($"u{i}"));
        }

        await writer.ExpectSilenceAsync();
        var late = await ConnectAsync();

        var updates = await late.JoinAsync(page);

        updates.Select(u => System.Text.Encoding.UTF8.GetString(u)).ShouldBe(Enumerable.Range(0, 20).Select(i => $"u{i}"));
    }

    [Fact]
    public async Task One_socket_carries_several_pages_and_no_frame_crosses_from_one_page_to_another()
    {
        var a = await CreatePageAsync("A");
        var b = await CreatePageAsync("B");
        var both = await ConnectAsync();
        await both.JoinAsync(a);
        await both.JoinAsync(b);
        var onlyA = await ConnectAsync();
        await onlyA.JoinAsync(a);
        var writer = await ConnectAsync();
        await writer.JoinAsync(a);
        await writer.JoinAsync(b);

        await writer.SendUpdateAsync(b, Bytes("to b"));
        await writer.SendUpdateAsync(a, Bytes("to a"));

        var first = await both.ReceiveAsync();
        var second = await both.ReceiveAsync();
        ShouldBeUpdate(first, b, "to b");
        ShouldBeUpdate(second, a, "to a");
        ShouldBeUpdate(await onlyA.ReceiveAsync(), a, "to a");
        await onlyA.ExpectSilenceAsync();
        (await StoredAsync(a)).Select(u => u.Data).ShouldBe([Bytes("to a")]);
        (await StoredAsync(b)).Select(u => u.Data).ShouldBe([Bytes("to b")]);
    }

    [Fact]
    public async Task A_subscriber_who_joins_while_updates_stream_in_gets_each_one_exactly_once()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);
        var expected = Enumerable.Range(0, 300).Select(i => $"u{i:D3}").ToList();

        var writing = Task.Run(async () =>
        {
            foreach (var text in expected)
            {
                await writer.SendUpdateAsync(page, Bytes(text));
            }
        });
        // Join somewhere in the middle of the stream.
        await WaitUntilAsync(async () => (await StoredAsync(page)).Count >= 100);
        var reader = await ConnectAsync();
        var seen = (await reader.JoinAsync(page)).Select(u => System.Text.Encoding.UTF8.GetString(u)).ToList();
        while (seen.Count < expected.Count)
        {
            var next = await reader.ReceiveAsync();
            next.Type.ShouldBe(MessageType.Update, next.ToString());
            seen.Add(System.Text.Encoding.UTF8.GetString(next.Payload));
        }

        await writing;
        seen.ShouldBe(expected);
        await reader.ExpectSilenceAsync();
    }

    [Fact]
    public async Task After_a_restart_the_log_comes_from_the_database_alone()
    {
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);
        await writer.SendUpdateAsync(page, Bytes("before the restart"));
        await writer.ExpectSilenceAsync();
        await writer.DisposeAsync();
        _sockets.Remove(writer);
        await _factory.DisposeAsync();

        _factory = new InkpactFactory(_connectionString);
        _jos = await SyncUser.LogInAsync(_factory, AuthTestExtensions.Email);
        var client = await ConnectAsync();

        (await client.JoinAsync(page)).Select(u => u.ToArray()).ShouldBe([Bytes("before the restart")]);
    }

    [Fact]
    public async Task A_page_nobody_receives_is_forgotten_by_the_hub()
    {
        var a = await CreatePageAsync("A");
        var b = await CreatePageAsync("B");
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        await first.JoinAsync(a);
        await first.JoinAsync(b);
        await second.JoinAsync(a);
        Hub.OpenPages.ShouldBe(2);

        await first.UnsubscribeAsync(b);
        await WaitUntilAsync(() => Hub.OpenPages == 1);
        await first.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await second.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        await WaitUntilAsync(() => Hub.OpenPages == 0 && Hub.OpenConnections == 0);
    }

    [Fact]
    public async Task Pages_come_back_after_being_forgotten_with_everything_stored()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        await client.JoinAsync(page);
        await client.SendUpdateAsync(page, Bytes("one"));
        await client.UnsubscribeAsync(page);
        await WaitUntilAsync(() => Hub.OpenPages == 0);

        (await client.JoinAsync(page)).Select(u => u.ToArray()).ShouldBe([Bytes("one")]);
    }

    // ---- presence -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Presence_is_relayed_shown_to_newcomers_and_removed_when_a_socket_closes_but_never_stored()
    {
        var page = await CreatePageAsync();
        var leaving = await ConnectAsync();
        var staying = await ConnectAsync();
        await leaving.JoinAsync(page);
        await staying.JoinAsync(page);

        await leaving.SendAwarenessAsync(page, new AwarenessEntry(7, 1, """{"user":"jos"}"""));
        var relayed = await staying.ReceiveAsync();
        relayed.Type.ShouldBe(MessageType.Awareness);
        AwarenessEntry.Parse(relayed.Payload).ShouldBe([new AwarenessEntry(7, 1, """{"user":"jos"}""")]);

        var newcomer = await ConnectAsync();
        await newcomer.JoinAsync(page);
        var present = await newcomer.ReceiveAsync();
        AwarenessEntry.Parse(present.Payload).ShouldBe([new AwarenessEntry(7, 1, """{"user":"jos"}""")]);

        await leaving.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        var removal = await staying.ReceiveAsync();
        // Same clock and a null state: y-protocols removes the client, and accepts it again at the next clock.
        AwarenessEntry.Parse(removal.Payload).ShouldBe([new AwarenessEntry(7, 1, AwarenessEntry.Removed)]);
        (await StoredAsync(page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_viewer_may_show_that_they_are_there()
    {
        var page = await CreatePageAsync();
        var jos = await ConnectAsync();
        await jos.JoinAsync(page);
        var viewer = await ConnectAsync(await OtherPersonAsync(WorkspaceRole.Viewer));
        await viewer.JoinAsync(page);

        await viewer.SendAwarenessAsync(page, new AwarenessEntry(9, 1, """{"user":"viewer"}"""));

        (await jos.ReceiveAsync()).Type.ShouldBe(MessageType.Awareness);
    }

    // ---- limits and bad input -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ping_is_answered_with_a_pong()
    {
        var client = await ConnectAsync();

        await client.SendAsync([(byte)MessageType.Ping]);

        (await client.ReceiveAsync()).Type.ShouldBe(MessageType.Pong);
    }

    [Fact]
    public async Task A_message_over_the_size_limit_closes_the_socket()
    {
        await StartAsync(new() { ["Sync:MaxMessageBytes"] = "1024" });
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        await client.JoinAsync(page);

        await client.SendUpdateAsync(page, new byte[2048]);

        var close = await client.ExpectCloseAsync();
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.MessageTooBig);
        (await StoredAsync(page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_socket_that_sends_too_fast_is_closed()
    {
        await StartAsync(new() { ["Sync:MessagesPerSecond"] = "1", ["Sync:MessageBurst"] = "5" });
        var client = await ConnectAsync();

        for (var i = 0; i < 20; i++)
        {
            await client.SendAsync([(byte)MessageType.Ping]);
        }

        var close = await client.ExpectCloseAsync();
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        close.CloseReason.ShouldBe(SyncClose.RateLimitedReason);
    }

    [Fact]
    public async Task A_text_message_closes_the_socket()
    {
        var client = await ConnectAsync();

        await client.Socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);

        (await client.ExpectCloseAsync()).CloseReason.ShouldBe(SyncClose.ProtocolErrorReason);
    }

    [Theory]
    [InlineData(new byte[] { 9 })] // an unknown type
    [InlineData(new byte[] { (byte)MessageType.Pong })] // a type only the server sends
    [InlineData(new byte[] { (byte)MessageType.Subscribe, 3, (byte)'a', (byte)'b', (byte)'c', 0 })] // not a page id
    [InlineData(new byte[] { (byte)MessageType.Subscribe, 200 })] // a length past the end
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 })] // a number that never ends
    public async Task A_malformed_message_closes_the_socket_with_a_policy_violation(byte[] message)
    {
        var client = await ConnectAsync();

        await client.SendAsync(message);

        var close = await client.ExpectCloseAsync();
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        close.CloseReason.ShouldBe(SyncClose.ProtocolErrorReason);
    }

    [Fact]
    public async Task A_page_id_in_another_spelling_is_refused_so_that_one_page_has_one_name()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)MessageType.Subscribe);
        writer.WriteVarString(page.ToString("D").ToUpperInvariant());
        writer.WriteVarUint8Array([]);

        await client.SendAsync(writer.ToArray());

        (await client.ExpectCloseAsync()).CloseReason.ShouldBe(SyncClose.ProtocolErrorReason);
    }

    // ---- sessions -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logging_out_closes_the_sockets_of_that_session_and_only_those()
    {
        var otherBrowser = await _jos.LogInAgainAsync();
        var mine = await ConnectAsync();
        var theirs = await ConnectAsync(otherBrowser);

        (await _jos.Http.PostJsonAsync("/api/auth/logout")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var close = await mine.ExpectCloseAsync();
        close.CloseStatus.ShouldBe(SyncClose.SessionEnded);
        close.CloseReason.ShouldBe(SyncClose.SessionEndedReason);
        await theirs.ExpectSilenceAsync();
    }

    [Fact]
    public async Task Logging_out_everywhere_closes_every_socket_of_that_person_and_nobody_elses()
    {
        var otherBrowser = await _jos.LogInAgainAsync();
        var mine = await ConnectAsync();
        var mineElsewhere = await ConnectAsync(otherBrowser);
        var someoneElse = await ConnectAsync(await OtherPersonAsync(WorkspaceRole.Editor));

        (await _jos.Http.PostJsonAsync("/api/auth/logout-everywhere")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await mine.ExpectCloseAsync()).CloseStatus.ShouldBe(SyncClose.SessionEnded);
        (await mineElsewhere.ExpectCloseAsync()).CloseStatus.ShouldBe(SyncClose.SessionEnded);
        await someoneElse.ExpectSilenceAsync();
    }

    [Fact]
    public async Task A_socket_whose_session_disappears_some_other_way_is_closed_at_the_next_check()
    {
        await StartAsync(new() { ["Sync:SessionCheckInterval"] = "00:00:00.200" });
        var client = await ConnectAsync();
        await client.ExpectSilenceAsync();

        await UseDbAsync(db => db.Sessions.ExecuteDeleteAsync());

        (await client.ExpectCloseAsync()).CloseStatus.ShouldBe(SyncClose.SessionEnded);
    }

    [Fact]
    public async Task A_server_that_stops_tells_its_sockets_why()
    {
        var client = await ConnectAsync();
        await client.ExpectSilenceAsync();

        var stopping = _factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        stopping.StopApplication();

        var close = await client.ExpectCloseAsync();
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        close.CloseReason.ShouldBe(SyncClose.ServerStoppingReason);
    }

    // ---- updates the server makes itself (the import replacing a note) -----------------------------------------------------

    private Task<bool> StoreAsync(Guid pageId, string text) =>
        Hub.PublishAsync(pageId, Bytes(text), async token =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<PageUpdateStore>();
            await store.AppendAsync(pageId, _jos.UserId, Bytes(text), token);
            return true;
        }, CancellationToken.None);

    [Fact]
    public async Task An_update_the_server_publishes_is_stored_and_sent_to_everyone_who_has_the_page_open()
    {
        var page = await CreatePageAsync();
        var first = await ConnectAsync();
        var second = await ConnectAsync(await _jos.LogInAgainAsync());
        await first.JoinAsync(page);
        await second.JoinAsync(page);

        await StoreAsync(page, "from the import");

        ShouldBeUpdate(await first.ReceiveAsync(MessageType.Update), page, "from the import");
        ShouldBeUpdate(await second.ReceiveAsync(MessageType.Update), page, "from the import");
        (await StoredAsync(page)).Select(u => u.Data).ShouldBe([Bytes("from the import")]);
    }

    [Fact]
    public async Task An_update_that_could_not_be_stored_is_not_sent()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        await client.JoinAsync(page);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Hub.PublishAsync(page, Bytes("never stored"), _ => throw new InvalidOperationException("the database is gone"), CancellationToken.None));

        await client.ExpectSilenceAsync();
        await Hub.PublishAsync(page, Bytes("after"), _ => Task.FromResult(true), CancellationToken.None);
        ShouldBeUpdate(await client.ReceiveAsync(MessageType.Update), page, "after");
    }

    [Fact]
    public async Task An_update_the_caller_decides_not_to_store_after_all_is_not_sent()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        await client.JoinAsync(page);

        (await Hub.PublishAsync(page, Bytes("not after all"), _ => Task.FromResult(false), CancellationToken.None)).ShouldBeFalse();

        await client.ExpectSilenceAsync();
    }

    [Fact]
    public async Task A_publish_that_is_cancelled_before_it_gets_the_page_leaves_no_room_behind()
    {
        var page = await CreatePageAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Hub.PublishAsync(page, Bytes("never"), _ => Task.FromResult(true), cancelled.Token));

        await WaitUntilAsync(() => Hub.OpenPages == 0);
        (await StoredAsync(page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_nobody_has_open_is_stored_just_the_same_and_leaves_no_room_behind()
    {
        var page = await CreatePageAsync();

        await StoreAsync(page, "quiet");

        (await StoredAsync(page)).Select(u => u.Data).ShouldBe([Bytes("quiet")]);
        Hub.OpenPages.ShouldBe(0);
    }

    [Fact]
    public async Task Someone_who_opens_the_page_while_an_update_is_published_gets_it_once()
    {
        var page = await CreatePageAsync();
        var client = await ConnectAsync();
        var committing = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var publishing = Hub.PublishAsync(page, Bytes("one"), async token =>
        {
            committing.SetResult();
            await release.Task;
            await using var scope = _factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PageUpdateStore>().AppendAsync(page, _jos.UserId, Bytes("one"), token);
            return true;
        }, CancellationToken.None);
        await committing.Task;
        var joining = client.JoinAsync(page);
        release.SetResult();
        await publishing;

        // Stored before the sync was made, or relayed after it: either way once.
        var synced = (await joining).Select(u => Convert.ToHexString(u)).ToList();
        var relayed = synced.Count == 0;
        if (relayed)
        {
            ShouldBeUpdate(await client.ReceiveAsync(MessageType.Update), page, "one");
        }
        else
        {
            synced.ShouldBe([Convert.ToHexString(Bytes("one"))]);
        }

        await client.ExpectSilenceAsync();
    }

    private static void ShouldBeUpdate(Received received, Guid pageId, string text)
    {
        received.Type.ShouldBe(MessageType.Update, received.ToString());
        received.PageId.ShouldBe(pageId);
        received.Payload.ShouldBe(Bytes(text));
    }

    private static async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + SyncTestClient.Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(20);
        }
    }
}
