using System.Net.WebSockets;
using System.Threading.Channels;

namespace Inkpact.Api.Sync;

/// <summary>Close codes and reasons the hub uses. The reasons are codes too, for the client to act on.</summary>
public static class SyncClose
{
    /// <summary>The session was logged out or expired. The client goes to the login page instead of reconnecting.</summary>
    public const WebSocketCloseStatus SessionEnded = (WebSocketCloseStatus)4401;

    public const string SessionEndedReason = "session_ended";
    public const string ProtocolErrorReason = "protocol_error";
    public const string RateLimitedReason = "rate_limited";
    public const string MessageTooBigReason = "message_too_big";
    public const string ServerErrorReason = "server_error";
    public const string ServerStoppingReason = "server_stopping";
}

/// <summary>
/// One client socket: who it belongs to, the pages it receives, and its outgoing queue. A WebSocket allows one send at a
/// time, so everything going out passes through the queue and one send loop. The queue is bounded: a client that falls
/// too far behind is dropped, and resyncs when it reconnects.
/// </summary>
public sealed class SyncConnection(WebSocket socket, Guid userId, string sessionKey, SyncOptions options, CancellationToken requestAborted)
    : IDisposable
{
    private readonly Channel<Outgoing> _outgoing = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _abort = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
    private int _queuedMessages;
    private long _queuedBytes;
    private int _closeRequested;

    public WebSocket Socket => socket;

    public Guid UserId => userId;

    public string SessionKey => sessionKey;

    /// <summary>Cancelled when the socket must go now: the client is too slow, or did not answer a close in time.</summary>
    public CancellationToken Aborted => _abort.Token;

    /// <summary>The pages this connection receives. Only its own receive loop touches this.</summary>
    internal Dictionary<Guid, Subscription> Pages { get; } = [];

    /// <summary>Queues a message. Returns false, and drops the connection, when the client is too far behind.</summary>
    public bool Enqueue(byte[] message)
    {
        var count = Interlocked.Increment(ref _queuedMessages);
        var bytes = Interlocked.Add(ref _queuedBytes, message.Length);
        if (count > options.MaxQueuedMessages || bytes > options.MaxQueuedBytes)
        {
            Abort();
            return false;
        }

        return _outgoing.Writer.TryWrite(new Outgoing(message, null, null));
    }

    /// <summary>
    /// Sends a close frame after what is already queued, and stops sending. The client answers it and the receive loop
    /// ends; a client that does not answer within <see cref="SyncOptions.CloseTimeout"/> is dropped. Only the first
    /// request counts.
    /// </summary>
    public void RequestClose(WebSocketCloseStatus status, string reason)
    {
        if (Interlocked.Exchange(ref _closeRequested, 1) == 1)
        {
            return;
        }

        _outgoing.Writer.TryWrite(new Outgoing(null, status, reason));
        _outgoing.Writer.TryComplete();
    }

    /// <summary>Drops the socket without a close frame.</summary>
    public void Abort()
    {
        _outgoing.Writer.TryComplete();
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    public async Task RunSendLoopAsync()
    {
        try
        {
            await foreach (var item in _outgoing.Reader.ReadAllAsync(Aborted))
            {
                if (item.Message is { } message)
                {
                    Interlocked.Decrement(ref _queuedMessages);
                    Interlocked.Add(ref _queuedBytes, -message.Length);
                    await socket.SendAsync(message, WebSocketMessageType.Binary, true, Aborted);
                    continue;
                }

                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(item.Status!.Value, item.Reason, Aborted);
                }

                _abort.CancelAfter(options.CloseTimeout);
                return;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The receive loop notices the broken socket and cleans up.
        }
    }

    public void Dispose() => _abort.Dispose();

    private sealed record Outgoing(byte[]? Message, WebSocketCloseStatus? Status, string? Reason);
}

/// <summary>A page a connection receives, and whether it may change it.</summary>
internal sealed record Subscription(PageRoom Room, bool CanEdit);
