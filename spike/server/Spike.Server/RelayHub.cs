using System.Net.WebSockets;
using System.Threading.Channels;

namespace Spike.Server;

/// <summary>
/// Relays binary messages between all sockets subscribed to the same page.
/// Messages are opaque Yjs payloads; the hub never decodes them.
/// </summary>
public sealed class RelayHub(ILogger<RelayHub> logger)
{
    private const int MaxMessageBytes = 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, HashSet<Connection>> _pages = [];

    public async Task RunAsync(string pageId, WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new Connection(socket);
        Join(pageId, connection);
        var sendLoop = connection.RunSendLoopAsync(cancellationToken);

        try
        {
            await ReceiveLoopAsync(pageId, connection, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Connection on page {PageId} dropped", pageId);
        }
        finally
        {
            Leave(pageId, connection);
            connection.CompleteSending();
            await sendLoop;
        }

        if (socket.State == WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
    }

    private async Task ReceiveLoopAsync(string pageId, Connection connection, CancellationToken cancellationToken)
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
                await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, cancellationToken);
                return;
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                Broadcast(pageId, connection, message.ToArray());
            }

            message.SetLength(0);
        }
    }

    private void Join(string pageId, Connection connection)
    {
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageId, out var peers))
            {
                peers = [];
                _pages[pageId] = peers;
            }

            peers.Add(connection);
        }
    }

    private void Leave(string pageId, Connection connection)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageId, out var peers) && peers.Remove(connection) && peers.Count == 0)
            {
                _pages.Remove(pageId);
            }
        }
    }

    private void Broadcast(string pageId, Connection sender, byte[] message)
    {
        Connection[] targets;
        lock (_lock)
        {
            if (!_pages.TryGetValue(pageId, out var peers))
            {
                return;
            }

            targets = [.. peers.Where(peer => peer != sender)];
        }

        foreach (var target in targets)
        {
            target.Enqueue(message);
        }
    }

    /// <summary>
    /// One socket plus an outgoing queue, because a WebSocket allows only one SendAsync at a time.
    /// Spike only: the queue is unbounded, so a slow client can grow memory (fix in 1.09).
    /// </summary>
    private sealed class Connection(WebSocket socket)
    {
        private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true });

        public WebSocket Socket { get; } = socket;

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
