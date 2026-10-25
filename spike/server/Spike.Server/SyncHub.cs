using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.WebSockets;
using System.Threading.Channels;
using Spike.Server.Protocol;

namespace Spike.Server;

/// <summary>
/// One multiplexed socket per client session; every frame carries its page id.
/// Yjs updates are opaque: stored in order and relayed, never decoded.
/// </summary>
public sealed class SyncHub(IUpdateStore store, ILogger<SyncHub> logger)
{
    private const int MaxMessageBytes = 1024 * 1024;

    // Spike only: pages are never evicted. Each entry is a lock, a small set and an awareness cache.
    private readonly ConcurrentDictionary<string, Page> _pages = new();

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new Connection(socket);
        var sendLoop = connection.RunSendLoopAsync(cancellationToken);
        var closeStatus = WebSocketCloseStatus.NormalClosure;

        try
        {
            await ReceiveLoopAsync(connection, cancellationToken);
        }
        catch (ProtocolException ex)
        {
            logger.LogWarning("Closing connection after protocol error: {Message}", ex.Message);
            closeStatus = WebSocketCloseStatus.PolicyViolation;
        }
        catch (DbException ex)
        {
            // The update was not stored and not broadcast; the client's reconnect diff resends it.
            logger.LogError(ex, "Storing an update failed; closing connection");
            closeStatus = WebSocketCloseStatus.InternalServerError;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Connection dropped");
        }
        finally
        {
            foreach (var pageId in connection.Pages.ToArray())
            {
                await LeaveAsync(pageId, connection);
            }

            connection.CompleteSending();
            await sendLoop;
        }

        await CloseAsync(socket, closeStatus);
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await connection.Socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new ProtocolException("Message too big");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await HandleAsync(connection, message.ToArray(), cancellationToken);
            }

            message.SetLength(0);
        }
    }

    private async Task HandleAsync(Connection connection, byte[] message, CancellationToken cancellationToken)
    {
        // A phone that loses Wi-Fi never closes its socket, so clients ping and give up on silence.
        if (message is [(byte)MessageType.Ping])
        {
            connection.Enqueue([(byte)MessageType.Pong]);
            return;
        }

        var frame = Frame.Parse(message);
        switch (frame.Type)
        {
            case MessageType.Subscribe:
                if (connection.Pages.Add(frame.PageId))
                {
                    await SubscribeAsync(frame.PageId, connection, cancellationToken);
                }

                break;

            case MessageType.Unsubscribe:
                if (connection.Pages.Remove(frame.PageId))
                {
                    await LeaveAsync(frame.PageId, connection);
                }

                break;

            case MessageType.Update:
                await WithPageAsync(RequireSubscribed(connection, frame), async page =>
                {
                    // Stored before it is relayed: a peer never sees an update the log does not have.
                    await store.AppendAsync(frame.PageId, frame.Payload.ToArray(), cancellationToken);
                    page.Broadcast(message, except: connection);
                }, cancellationToken);
                break;

            case MessageType.Awareness:
                await WithPageAsync(RequireSubscribed(connection, frame), page =>
                {
                    page.Awareness.Apply(frame.Payload, connection);
                    page.Broadcast(message, except: connection);
                    return Task.CompletedTask;
                }, cancellationToken);
                break;

            default:
                throw new ProtocolException($"Clients may not send {frame.Type}");
        }
    }

    /// <summary>
    /// Registering, loading and queueing the sync happen under the page lock, and so does every
    /// append-and-broadcast. A new subscriber therefore gets each update exactly once: in the
    /// sync if it was stored before, live if after.
    /// </summary>
    private async Task SubscribeAsync(string pageId, Connection connection, CancellationToken cancellationToken)
    {
        var page = _pages.GetOrAdd(pageId, _ => new Page());
        await WithPageAsync(page, async _ =>
        {
            page.Subscribers.Add(connection);
            var load = await store.LoadPageAsync(pageId, cancellationToken);
            connection.Enqueue(Frame.EncodeSync(pageId, load.All));

            var presence = page.Awareness.EncodeAll();
            if (presence is not null)
            {
                connection.Enqueue(Frame.Encode(MessageType.Awareness, pageId, presence));
            }
        }, cancellationToken);
    }

    private async Task LeaveAsync(string pageId, Connection connection)
    {
        if (!_pages.TryGetValue(pageId, out var page))
        {
            return;
        }

        // Not cancellable: cleanup must run even when the request was aborted.
        await WithPageAsync(page, _ =>
        {
            page.Subscribers.Remove(connection);
            var removal = page.Awareness.RemoveOwner(connection);
            if (removal is not null)
            {
                page.Broadcast(Frame.Encode(MessageType.Awareness, pageId, removal), except: connection);
            }

            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    private Page RequireSubscribed(Connection connection, Frame frame) =>
        connection.Pages.Contains(frame.PageId) && _pages.TryGetValue(frame.PageId, out var page)
            ? page
            : throw new ProtocolException($"{frame.Type} for a page the connection did not subscribe to");

    private static async Task WithPageAsync(Page page, Func<Page, Task> action, CancellationToken cancellationToken)
    {
        await page.Lock.WaitAsync(cancellationToken);
        try
        {
            await action(page);
        }
        finally
        {
            page.Lock.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await socket.CloseOutputAsync(status, null, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Already gone.
        }
    }

    private sealed class Page
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public HashSet<Connection> Subscribers { get; } = [];

        public AwarenessCache Awareness { get; } = new();

        public void Broadcast(byte[] message, Connection except)
        {
            foreach (var subscriber in Subscribers)
            {
                if (subscriber != except)
                {
                    subscriber.Enqueue(message);
                }
            }
        }
    }

    /// <summary>
    /// One socket plus an outgoing queue, because a WebSocket allows only one SendAsync at a time.
    /// <see cref="Pages"/> is only touched by this connection's own receive loop.
    /// Spike only: the queue is unbounded, so a slow client can grow memory (fix in 1.09).
    /// </summary>
    private sealed class Connection(WebSocket socket)
    {
        private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });

        public WebSocket Socket { get; } = socket;

        public HashSet<string> Pages { get; } = [];

        public void Enqueue(byte[] message) => _outgoing.Writer.TryWrite(message);

        public void CompleteSending() => _outgoing.Writer.TryComplete();

        public async Task RunSendLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var message in _outgoing.Reader.ReadAllAsync(cancellationToken))
                {
                    await Socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The receive loop notices the broken socket and cleans up.
            }
        }
    }
}
