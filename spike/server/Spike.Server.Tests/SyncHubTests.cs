using System.Net.WebSockets;
using Spike.Server.Protocol;

namespace Spike.Server.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SyncHubTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Subscribe_returns_stored_updates_in_order()
    {
        var page = PostgresFixture.NewPageId();
        await fixture.Store.AppendAsync(page, [1], CancellationToken.None);
        await fixture.Store.AppendAsync(page, [2], CancellationToken.None);

        using var client = await TestClient.ConnectAsync(fixture);
        await client.SubscribeAsync(page);

        Assert.Equal([[1], [2]], await client.ReceiveSyncAsync(page));
    }

    [Fact]
    public async Task Update_is_stored_and_relayed_to_peers_but_not_echoed()
    {
        var page = PostgresFixture.NewPageId();
        using var alice = await TestClient.ConnectAsync(fixture);
        using var bob = await TestClient.ConnectAsync(fixture);
        await alice.SubscribeAsync(page);
        await bob.SubscribeAsync(page);
        await alice.ReceiveSyncAsync(page);
        await bob.ReceiveSyncAsync(page);

        await alice.SendAsync(Frame.Encode(MessageType.Update, page, [42]));
        var relayed = await bob.ReceiveAsync();

        Assert.Equal((MessageType.Update, page), (relayed.Type, relayed.PageId));
        Assert.Equal([42], relayed.Payload.ToArray());
        Assert.Equal([[42]], await fixture.Store.LoadAsync(page, CancellationToken.None));

        // An echo would be queued for the sender before this reply.
        await bob.SendAsync(Frame.Encode(MessageType.Update, page, [43]));
        Assert.Equal([43], (await alice.ReceiveAsync()).Payload.ToArray());
    }

    [Fact]
    public async Task One_socket_carries_several_pages_and_frames_keep_their_page_id()
    {
        var x = PostgresFixture.NewPageId();
        var y = PostgresFixture.NewPageId();
        using var multi = await TestClient.ConnectAsync(fixture);
        using var onlyY = await TestClient.ConnectAsync(fixture);
        await multi.SubscribeAsync(x);
        await multi.SubscribeAsync(y);
        await onlyY.SubscribeAsync(y);
        await multi.ReceiveSyncAsync(x);
        await multi.ReceiveSyncAsync(y);
        await onlyY.ReceiveSyncAsync(y);

        await multi.SendAsync(Frame.Encode(MessageType.Update, x, [1]));
        await multi.SendAsync(Frame.Encode(MessageType.Update, y, [2]));

        var received = await onlyY.ReceiveAsync();
        Assert.Equal((y, 2), (received.PageId, received.Payload.Span[0]));
        await onlyY.AssertNothingReceivedAsync();
        Assert.Equal([[1]], await fixture.Store.LoadAsync(x, CancellationToken.None));
    }

    [Fact]
    public async Task Update_for_a_page_that_was_not_subscribed_closes_the_socket()
    {
        var page = PostgresFixture.NewPageId();
        using var client = await TestClient.ConnectAsync(fixture);

        await client.SendAsync(Frame.Encode(MessageType.Update, page, [1]));

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, await client.ReceiveCloseAsync());
        Assert.Empty(await fixture.Store.LoadAsync(page, CancellationToken.None));
    }

    [Fact]
    public async Task Subscriber_joining_mid_stream_gets_every_update_exactly_once()
    {
        const int total = 300;
        var page = PostgresFixture.NewPageId();
        using var writer = await TestClient.ConnectAsync(fixture);
        using var reader = await TestClient.ConnectAsync(fixture);
        await writer.SubscribeAsync(page);
        await writer.ReceiveSyncAsync(page);

        var writing = Task.Run(async () =>
        {
            for (var i = 0; i < total; i++)
            {
                await writer.SendAsync(Frame.Encode(MessageType.Update, page, BitConverter.GetBytes(i)));
            }
        });

        // Join while the writer is busy, so some updates land in the sync and some arrive live.
        while (await fixture.Store.CountAsync(page, CancellationToken.None) < total / 3)
        {
            await Task.Delay(1);
        }

        await reader.SubscribeAsync(page);
        var seen = (await reader.ReceiveSyncAsync(page)).Select(update => BitConverter.ToInt32(update)).ToList();
        var fromSync = seen.Count;
        while (seen.Count < total)
        {
            seen.Add(BitConverter.ToInt32((await reader.ReceiveAsync()).Payload.Span));
        }

        await writing;
        Assert.InRange(fromSync, 1, total - 1);
        Assert.Equal(Enumerable.Range(0, total), seen);
        await reader.AssertNothingReceivedAsync();
    }

    [Fact]
    public async Task New_subscriber_gets_cached_presence_and_peers_see_removal_when_a_connection_drops()
    {
        var page = PostgresFixture.NewPageId();
        using var watcher = await TestClient.ConnectAsync(fixture);
        using var alice = await TestClient.ConnectAsync(fixture);
        using var late = await TestClient.ConnectAsync(fixture);
        await watcher.SubscribeAsync(page);
        await watcher.ReceiveSyncAsync(page);
        await alice.SubscribeAsync(page);
        await alice.ReceiveSyncAsync(page);

        var presence = AwarenessEntry.Encode([new AwarenessEntry(7, 1, """{"user":{"name":"jos"}}""")]);
        await alice.SendAsync(Frame.Encode(MessageType.Awareness, page, presence));
        Assert.Equal(presence, (await watcher.ReceiveAsync()).Payload.ToArray());

        await late.SubscribeAsync(page);
        await late.ReceiveSyncAsync(page);
        var cached = await late.ReceiveAsync();
        Assert.Equal(MessageType.Awareness, cached.Type);
        Assert.Equal(presence, cached.Payload.ToArray());

        await alice.CloseAsync();

        AwarenessEntry[] removal = [new(7, 1, AwarenessEntry.Removed)];
        Assert.Equal(removal, AwarenessEntry.Parse((await watcher.ReceiveAsync()).Payload));
        Assert.Equal(removal, AwarenessEntry.Parse((await late.ReceiveAsync()).Payload));
    }

    private sealed class TestClient : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private readonly ClientWebSocket _socket = new();

        public static async Task<TestClient> ConnectAsync(PostgresFixture fixture)
        {
            var client = new TestClient();
            await client._socket.ConnectAsync(fixture.WebSocketUri, CancellationToken.None);
            return client;
        }

        public Task SubscribeAsync(string pageId) => SendAsync(Frame.Encode(MessageType.Subscribe, pageId));

        public async Task SendAsync(byte[] frame)
        {
            using var cts = new CancellationTokenSource(Timeout);
            await _socket.SendAsync(frame, WebSocketMessageType.Binary, true, cts.Token);
        }

        public async Task<IReadOnlyList<byte[]>> ReceiveSyncAsync(string pageId)
        {
            var frame = await ReceiveAsync();
            Assert.Equal((MessageType.Sync, pageId), (frame.Type, frame.PageId));
            return Frame.ReadSyncUpdates(frame.Payload);
        }

        public async Task<Frame> ReceiveAsync()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var (message, _) = await ReceiveRawAsync(cts.Token);
            return Frame.Parse(message);
        }

        public async Task<WebSocketCloseStatus?> ReceiveCloseAsync()
        {
            using var cts = new CancellationTokenSource(Timeout);
            var (_, type) = await ReceiveRawAsync(cts.Token);
            Assert.Equal(WebSocketMessageType.Close, type);
            return _socket.CloseStatus;
        }

        public async Task AssertNothingReceivedAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveRawAsync(cts.Token));
        }

        public Task CloseAsync() =>
            _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        public void Dispose() => _socket.Dispose();

        private async Task<(byte[] Message, WebSocketMessageType Type)> ReceiveRawAsync(CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();
            var buffer = new byte[16 * 1024];
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return (message.ToArray(), result.MessageType);
        }
    }
}
