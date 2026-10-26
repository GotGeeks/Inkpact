using System.Diagnostics;
using System.Net;
using Inkpact.Api.Crdt;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Received = Inkpact.Api.Tests.Testing.Received;
using static Inkpact.Api.Tests.Testing.SyncTestClient;

namespace Inkpact.Api.Tests.Sync;

/// <summary>
/// How a subscribe is answered: with the diff and the server's state vector when the Yjs sidecar is there, with the
/// stored log when it is not. The real sidecar and real Yjs updates from the fixtures; the tests of the hub's other
/// behaviour (SyncHubTests) run with the sidecar off, which is the log mode, and send opaque bytes.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SyncHubDiffTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private SyncUser _jos = null!;
    private Guid _workspaceId;
    private readonly List<IAsyncDisposable> _sockets = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var socket in _sockets)
        {
            await socket.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>Starts the app on a fresh database. With the sidecar on, waits until its process is up.</summary>
    private async Task StartAsync(bool sidecar = true, IYjsCodec? codec = null, Dictionary<string, string>? settings = null)
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), settings: settings, sidecar: sidecar, codec: codec);
        _jos = await SyncUser.SetUpAdministratorAsync(_factory);
        _workspaceId = await UseDbAsync(db => db.Workspaces.Select(w => w.Id).SingleAsync());
        if (sidecar && settings?.ContainsKey("Sidecar:NodePath") != true && settings?.ContainsKey("Sidecar:ScriptPath") != true)
        {
            await Sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        }
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private YjsSidecar Sidecar => _factory.Services.GetRequiredService<YjsSidecar>();

    private IYjsCodec Codec => _factory.Services.GetRequiredService<IYjsCodec>();

    private async Task<SyncTestClient> ConnectAsync()
    {
        var client = await _jos.ConnectAsync();
        _sockets.Add(client);
        return client;
    }

    private async Task<Guid> CreatePageAsync()
    {
        var response = await _jos.Http.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "Page" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<PageChange>())!.Page.Id;
    }

    /// <summary>Puts updates into the log as the hub would have, without sending them through a socket.</summary>
    private async Task StoreAsync(Guid pageId, IEnumerable<byte[]> updates) =>
        await UseDbAsync(async db =>
        {
            foreach (var data in updates)
            {
                db.PageUpdates.Add(new PageUpdate { PageId = pageId, UserId = _jos.UserId, Data = data, CreatedAt = DateTimeOffset.UtcNow });
            }

            await db.SaveChangesAsync();
            return 0;
        });

    private Task<List<byte[]>> StoredAsync(Guid pageId) =>
        UseDbAsync(db => db.PageUpdates.AsNoTracking().Where(u => u.PageId == pageId).OrderBy(u => u.Seq).Select(u => u.Data).ToListAsync());

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), $"gave up waiting for {what}");
            await Task.Delay(20);
        }
    }

    private async Task<byte[]> FingerprintAsync(IEnumerable<byte[]> updates) => await Codec.FingerprintAsync([.. updates], default);

    /// <summary>The sync answer is the stored log, as in the spike: no state vector, and the client works out the rest.</summary>
    private static void ShouldBeTheLog(Received sync, IReadOnlyList<byte[]> log)
    {
        sync.StateVector.ShouldNotBeNull().ShouldBeEmpty();
        sync.Updates.ShouldBe(log);
    }

    // ---- diff mode ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_state_vector_gets_one_update_whose_fingerprint_equals_the_logs()
    {
        await StartAsync();
        var page = await CreatePageAsync();
        var log = YjsFixture.Load("tiptap-small").UpdateBytes;
        await StoreAsync(page, log);

        var sync = await (await ConnectAsync()).SyncAsync(page);

        sync.Updates.Count.ShouldBe(1);
        sync.StateVector.ShouldNotBeEmpty();
        (await FingerprintAsync(sync.Updates)).ShouldBe(await FingerprintAsync(log));
    }

    [Fact]
    public async Task A_partial_state_vector_gets_a_diff_smaller_than_the_log_that_completes_the_clients_state()
    {
        await StartAsync();
        var page = await CreatePageAsync();
        var fixture = YjsFixture.Load("no-format-2k");
        var log = fixture.UpdateBytes;
        await StoreAsync(page, log);

        var sync = await (await ConnectAsync()).SyncAsync(page, fixture.PartialStateVectorBytes);

        sync.Updates.Count.ShouldBe(1);
        sync.Updates[0].Length.ShouldBeLessThan(log.Sum(u => u.Length));
        sync.Updates[0].Length.ShouldBeLessThan((await Codec.CompactAsync(log, default)).State.Length);
        // The client's state plus the diff is the stored page.
        (await FingerprintAsync([.. log.Take(fixture.PartialCount), sync.Updates[0]])).ShouldBe(await FingerprintAsync(log));
    }

    [Fact]
    public async Task The_returned_state_vector_is_the_one_of_the_stored_page()
    {
        await StartAsync();
        var page = await CreatePageAsync();
        var log = YjsFixture.Load("tiptap-small").UpdateBytes;
        await StoreAsync(page, log);

        var sync = await (await ConnectAsync()).SyncAsync(page);

        sync.StateVector.ShouldBe((await Codec.CompactAsync(log, default)).StateVector);
    }

    [Fact]
    public async Task A_page_nobody_has_written_to_is_answered_with_an_empty_log_without_asking_the_sidecar()
    {
        var codec = Substitute.For<IYjsCodec>();
        await StartAsync(sidecar: false, codec);
        var page = await CreatePageAsync();

        var sync = await (await ConnectAsync()).SyncAsync(page);

        ShouldBeTheLog(sync, []);
        _ = codec.DidNotReceiveWithAnyArgs().DiffAsync(default, default!, default);
    }

    [Fact]
    public async Task A_subscriber_who_joins_while_real_updates_stream_in_ends_with_the_whole_page()
    {
        // The twin of the 1.09 test with opaque bytes: each update reaches the newcomer in the sync or live. Duplicates
        // would not show in a fingerprint, so the count of the live part is checked as well: it is what was stored after.
        await StartAsync();
        var page = await CreatePageAsync();
        var log = YjsFixture.Load("tiptap-small").UpdateBytes;
        var writer = await ConnectAsync();
        await writer.JoinAsync(page);

        var writing = Task.Run(async () =>
        {
            foreach (var update in log)
            {
                await writer.SendUpdateAsync(page, update);
            }
        });
        await WaitUntilAsync(async () => (await StoredAsync(page)).Count >= 100, "a third of the updates to be stored");
        var reader = await ConnectAsync();
        var sync = await reader.SyncAsync(page);
        await writing;
        await WaitUntilAsync(async () => (await StoredAsync(page)).Count == log.Count, "all updates to be stored");

        // Everything stored is relayed before the pong that answers a ping sent after it.
        await reader.SendAsync([(byte)MessageType.Ping]);
        var live = new List<byte[]>();
        for (var next = await reader.ReceiveAsync(); next.Type != MessageType.Pong; next = await reader.ReceiveAsync())
        {
            next.Type.ShouldBe(MessageType.Update, next.ToString());
            live.Add(next.Payload);
        }

        sync.Updates.Count.ShouldBe(1);
        (await FingerprintAsync([sync.Updates[0], .. live])).ShouldBe(await FingerprintAsync(log));
        live.Count.ShouldBeLessThan(log.Count);
    }

    // ---- log mode ----------------------------------------------------------------------------------------------------

    public static TheoryData<string> CodecFailures => ["unavailable", "rejected"];

    [Theory]
    [MemberData(nameof(CodecFailures))]
    public async Task When_the_codec_fails_the_subscriber_gets_the_stored_log(string failure)
    {
        var codec = Substitute.For<IYjsCodec>();
        codec.DiffAsync(default, default!, default).ThrowsAsyncForAnyArgs(
            failure == "unavailable" ? new YjsUnavailableException("restarting") : new YjsRejectedException("bad update"));
        await StartAsync(sidecar: false, codec);
        var page = await CreatePageAsync();
        var log = new List<byte[]> { Bytes("one"), Bytes("two") };
        await StoreAsync(page, log);

        var sync = await (await ConnectAsync()).SyncAsync(page);

        ShouldBeTheLog(sync, log);
    }

    [Fact]
    public async Task A_codec_that_hangs_past_the_diff_timeout_gives_the_log_in_time()
    {
        var codec = Substitute.For<IYjsCodec>();
        codec.DiffAsync(default, default!, default).ReturnsForAnyArgs(async call =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, call.ArgAt<CancellationToken>(2));
            return new YjsDiff([], []);
        });
        await StartAsync(sidecar: false, codec, new() { ["Sidecar:DiffTimeout"] = "00:00:00.300" });
        var page = await CreatePageAsync();
        var log = new List<byte[]> { Bytes("one") };
        await StoreAsync(page, log);

        var stopwatch = Stopwatch.StartNew();
        var sync = await (await ConnectAsync()).SyncAsync(page);

        ShouldBeTheLog(sync, log);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task With_the_sidecar_stopped_a_subscribe_still_gets_the_log()
    {
        await StartAsync();
        var page = await CreatePageAsync();
        var log = YjsFixture.Load("tiptap-small").UpdateBytes;
        await StoreAsync(page, log);
        await Sidecar.StopAsync(CancellationToken.None);

        var sync = await (await ConnectAsync()).SyncAsync(page);

        ShouldBeTheLog(sync, log);
    }

    [Fact]
    public async Task After_a_crash_a_subscribe_gets_the_log_and_the_sidecar_comes_back_on_its_own()
    {
        await StartAsync();
        var page = await CreatePageAsync();
        var log = YjsFixture.Load("tiptap-small").UpdateBytes;
        await StoreAsync(page, log);
        var before = Sidecar.ProcessId;
        Sidecar.KillForTest();
        await WaitUntilAsync(() => Task.FromResult(Sidecar.ProcessId is null), "the sidecar to be noticed dead");

        // Inside the second of backoff before the restart.
        var duringTheRestart = await (await ConnectAsync()).SyncAsync(page);
        await Sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        var afterTheRestart = await (await ConnectAsync()).SyncAsync(page);

        ShouldBeTheLog(duringTheRestart, log);
        afterTheRestart.Updates.Count.ShouldBe(1);
        afterTheRestart.StateVector.ShouldNotBeEmpty();
        Sidecar.ProcessId.ShouldNotBe(before);
    }

    [Fact]
    public async Task A_log_that_does_not_decode_in_yjs_gives_the_log_and_leaves_the_sidecar_running()
    {
        await StartAsync();
        var broken = await CreatePageAsync();
        var fine = await CreatePageAsync();
        var brokenLog = new List<byte[]> { Bytes("not"), Bytes("yjs") };
        await StoreAsync(broken, brokenLog);
        await StoreAsync(fine, YjsFixture.Load("tiptap-small").UpdateBytes);
        var before = Sidecar.ProcessId;
        var client = await ConnectAsync();

        var sync = await client.SyncAsync(broken);
        var other = await client.SyncAsync(fine);

        ShouldBeTheLog(sync, brokenLog);
        other.Updates.Count.ShouldBe(1);
        other.StateVector.ShouldNotBeEmpty();
        Sidecar.ProcessId.ShouldBe(before);
    }

    [Fact]
    public async Task A_state_vector_that_is_not_one_gives_the_log_and_the_next_subscribe_is_unaffected()
    {
        await StartAsync();
        var page = await CreatePageAsync();
        var log = YjsFixture.Load("tiptap-small").UpdateBytes;
        await StoreAsync(page, log);
        var before = Sidecar.ProcessId;
        var client = await ConnectAsync();

        var garbage = await client.SyncAsync(page, [255, 255, 255]);
        await client.UnsubscribeAsync(page);
        var proper = await client.SyncAsync(page);

        ShouldBeTheLog(garbage, log);
        proper.Updates.Count.ShouldBe(1);
        Sidecar.ProcessId.ShouldBe(before);
    }

    [Fact]
    public async Task A_sidecar_that_hangs_costs_the_subscriber_the_diff_timeout_and_is_restarted()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sidecars", "silent-sidecar.mjs");
        await StartAsync(settings: new() { ["Sidecar:ScriptPath"] = script, ["Sidecar:DiffTimeout"] = "00:00:00.300" });
        await Sidecar.WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        var page = await CreatePageAsync();
        var log = new List<byte[]> { Bytes("one") };
        await StoreAsync(page, log);
        var before = Sidecar.ProcessId;

        var stopwatch = Stopwatch.StartNew();
        var sync = await (await ConnectAsync()).SyncAsync(page);

        ShouldBeTheLog(sync, log);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Task.FromResult(Sidecar.Starts == 2 && Sidecar.ProcessId is not null), "the restart");
        Sidecar.ProcessId.ShouldNotBe(before);
    }

    [Fact]
    public async Task The_app_starts_and_syncs_in_log_mode_when_node_is_missing()
    {
        await StartAsync(settings: new() { ["Sidecar:NodePath"] = Path.Combine(AppContext.BaseDirectory, "no-such-node") });
        var page = await CreatePageAsync();
        var writer = await ConnectAsync();
        var reader = await ConnectAsync();
        await writer.JoinAsync(page);

        await writer.SendUpdateAsync(page, Bytes("hello"));
        await writer.ExpectSilenceAsync();
        var sync = await reader.SyncAsync(page);

        (await _jos.Http.GetAsync("/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldBeTheLog(sync, [Bytes("hello")]);
        (await StoredAsync(page)).ShouldBe([Bytes("hello")]);
        Sidecar.Starts.ShouldBe(0);
    }

    [Fact]
    public async Task An_update_sent_while_a_subscribe_waits_on_the_sidecar_is_relayed_after_the_sync_and_not_lost()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var codec = Substitute.For<IYjsCodec>();
        codec.DiffAsync(default, default!, default).ReturnsForAnyArgs(async _ =>
        {
            // The first subscriber is let through at once; the second one waits for the gate.
            if (Interlocked.Increment(ref calls) > 1)
            {
                await gate.Task;
            }

            return new YjsDiff([1], [2]);
        });
        await StartAsync(sidecar: false, codec);
        var page = await CreatePageAsync();
        await StoreAsync(page, [Bytes("stored")]);
        var editor = await ConnectAsync();
        await editor.JoinAsync(page);
        var newcomer = await ConnectAsync();

        await newcomer.SubscribeAsync(page);
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) == 2), "the subscribe to reach the codec");
        await editor.SendUpdateAsync(page, Bytes("typed meanwhile"));
        await Task.Delay(200);
        (await StoredAsync(page)).Count.ShouldBe(1); // waits for the page lock like everything else on the page
        gate.SetResult();

        var sync = await newcomer.ReceiveAsync();
        var live = await newcomer.ReceiveAsync(MessageType.Update);
        sync.Type.ShouldBe(MessageType.Sync);
        sync.StateVector.ShouldBe([2]);
        sync.Updates.ShouldBe([new byte[] { 1 }]);
        live.Payload.ShouldBe(Bytes("typed meanwhile"));
        await WaitUntilAsync(async () => (await StoredAsync(page)).Count == 2, "the update to be stored");
        (await StoredAsync(page)).ShouldBe([Bytes("stored"), Bytes("typed meanwhile")]);
    }
}
