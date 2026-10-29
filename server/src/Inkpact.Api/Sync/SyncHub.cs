using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.WebSockets;
using System.Threading.RateLimiting;
using Inkpact.Api.Auth;
using Inkpact.Api.Crdt;
using Inkpact.Api.Permissions;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Sync;

/// <summary>
/// The pages that have subscribers right now. Everything that changes a page's subscribers, or stores and relays one of
/// its updates, happens under its lock; a page without subscribers is forgotten.
/// </summary>
internal sealed class PageRoom
{
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public HashSet<SyncConnection> Subscribers { get; } = [];

    public AwarenessCache Awareness { get; } = new();

    /// <summary>Set when the last subscriber left and the room was removed. A subscriber that finds it set starts over.</summary>
    public bool Closed { get; set; }

    public void Broadcast(byte[] message, SyncConnection? except)
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
/// The sync hub: one multiplexed socket per client session, and every frame carries its page id. Yjs updates are
/// opaque: stored in order and relayed, never decoded here. Only the Yjs sidecar decodes them, to answer a subscribe
/// with the diff the client is missing; when it cannot, the subscriber gets the stored log and works out itself what
/// the server lacks. Appending an update never depends on the sidecar.
/// </summary>
public sealed class SyncHub(
    PageUpdateStore store,
    IServiceScopeFactory scopes,
    PostgresTicketStore tickets,
    SessionEvents sessionEvents,
    SyncOptions options,
    IYjsCodec codec,
    SidecarOptions sidecarOptions,
    TimeProvider time,
    IHostApplicationLifetime lifetime,
    ILogger<SyncHub> logger)
{
    private readonly ConcurrentDictionary<Guid, PageRoom> _rooms = new();
    private readonly ConcurrentDictionary<Guid, int> _connectionsPerUser = new();
    private readonly ConcurrentDictionary<SyncConnection, byte> _connections = new();

    /// <summary>Pages that have at least one subscriber.</summary>
    public int OpenPages => _rooms.Count;

    public int OpenConnections => _connections.Count;

    /// <summary>
    /// Stores an update that the server itself made for a page (the Joplin import replacing a note's content) and relays it
    /// to everyone who has the page open, the way an update from a client is. <paramref name="commit"/> does the storing,
    /// in the caller's own transaction, so that the update and whatever else belongs with it are written together. It runs
    /// under the page's lock, so no client's update is stored meanwhile: what <paramref name="commit"/> reads of the page's
    /// log is the whole log until it returns. It returns false when it decided not to store the update after all; nothing is
    /// relayed then, nor when it throws. A page nobody has open is stored just the same. Returns whether it was published.
    /// </summary>
    public async Task<bool> PublishAsync(Guid pageId, byte[] update, Func<CancellationToken, Task<bool>> commit, CancellationToken cancellationToken)
    {
        var room = await EnterRoomAsync(pageId, cancellationToken);
        try
        {
            if (!await commit(cancellationToken))
            {
                return false;
            }

            room.Broadcast(Frame.Encode(MessageType.Update, pageId, update), except: null);
            return true;
        }
        finally
        {
            ExitRoom(pageId, room);
        }
    }

    /// <summary>Takes one of the user's connection slots. False when they have all their sockets open already.</summary>
    public bool TryReserveConnection(Guid userId)
    {
        while (true)
        {
            var current = _connectionsPerUser.GetOrAdd(userId, 0);
            if (current >= options.MaxConnectionsPerUser)
            {
                return false;
            }

            if (_connectionsPerUser.TryUpdate(userId, current + 1, current))
            {
                return true;
            }
        }
    }

    public void ReleaseConnection(Guid userId)
    {
        while (true)
        {
            var current = _connectionsPerUser.GetOrAdd(userId, 0);
            if (current <= 1)
            {
                if (_connectionsPerUser.TryRemove(new KeyValuePair<Guid, int>(userId, current)))
                {
                    return;
                }
            }
            else if (_connectionsPerUser.TryUpdate(userId, current - 1, current))
            {
                return;
            }
        }
    }

    public async Task RunAsync(WebSocket socket, Guid userId, string sessionKey, CancellationToken requestAborted)
    {
        using var connection = new SyncConnection(socket, userId, sessionKey, options, requestAborted);
        _connections.TryAdd(connection, 0);
        using var sessionListener = sessionEvents.Listen(
            sessionKey, userId, () => connection.RequestClose(SyncClose.SessionEnded, SyncClose.SessionEndedReason));
        using var stopping = lifetime.ApplicationStopping.Register(
            () => connection.RequestClose(WebSocketCloseStatus.EndpointUnavailable, SyncClose.ServerStoppingReason));
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.MessageBurst,
            TokensPerPeriod = options.MessagesPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

        var sendLoop = connection.RunSendLoopAsync();
        var sessionCheck = CheckSessionAsync(connection);
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        var closeReason = "";

        try
        {
            await ReceiveLoopAsync(connection, limiter);
        }
        catch (ProtocolException ex)
        {
            logger.LogInformation("Closing a sync connection after a protocol error: {Message}", ex.Message);
            (closeStatus, closeReason) = (WebSocketCloseStatus.PolicyViolation, SyncClose.ProtocolErrorReason);
        }
        catch (RateLimitedException)
        {
            logger.LogInformation("Closing a sync connection that sent too many messages");
            (closeStatus, closeReason) = (WebSocketCloseStatus.PolicyViolation, SyncClose.RateLimitedReason);
        }
        catch (MessageTooBigException)
        {
            (closeStatus, closeReason) = (WebSocketCloseStatus.MessageTooBig, SyncClose.MessageTooBigReason);
        }
        catch (DbException ex)
        {
            // The update was neither stored nor relayed; the client sends it again after it reconnects.
            logger.LogError(ex, "Storing or loading a page's updates failed; closing the sync connection");
            (closeStatus, closeReason) = (WebSocketCloseStatus.InternalServerError, SyncClose.ServerErrorReason);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            logger.LogDebug(ex, "A sync connection dropped");
        }
        finally
        {
            foreach (var pageId in connection.Pages.Keys.ToArray())
            {
                await LeaveAsync(connection, pageId);
            }

            _connections.TryRemove(connection, out _);
            connection.RequestClose(closeStatus, closeReason);
            await sendLoop;
            connection.Abort();
            await sessionCheck;
        }
    }

    private async Task ReceiveLoopAsync(SyncConnection connection, RateLimiter limiter)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await connection.Socket.ReceiveAsync(buffer.AsMemory(), connection.Aborted);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > options.MaxMessageBytes)
            {
                throw new MessageTooBigException();
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            using (var lease = limiter.AttemptAcquire())
            {
                if (!lease.IsAcquired)
                {
                    throw new RateLimitedException();
                }
            }

            // Text frames are not part of the protocol.
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new ProtocolException("Text message");
            }

            await HandleAsync(connection, message.ToArray());
            message.SetLength(0);
        }
    }

    private async Task HandleAsync(SyncConnection connection, byte[] message)
    {
        // A phone that loses its network never closes its socket, so clients ping and give up on silence.
        if (message is [(byte)MessageType.Ping])
        {
            connection.Enqueue([(byte)MessageType.Pong]);
            return;
        }

        var frame = Frame.Parse(message);
        switch (frame.Type)
        {
            case MessageType.Subscribe:
                await SubscribeAsync(connection, frame.PageId, frame.Payload);
                break;

            case MessageType.Unsubscribe:
                await LeaveAsync(connection, frame.PageId);
                break;

            case MessageType.Update:
                var subscription = RequireSubscribed(connection, frame);
                if (!subscription.CanEdit)
                {
                    connection.Enqueue(Frame.EncodeError(frame.PageId, SyncErrors.ReadOnly));
                    break;
                }

                await WithLockAsync(subscription.Room, async room =>
                {
                    // Stored before it is relayed: a peer never sees an update the log does not have.
                    await store.AppendAsync(frame.PageId, connection.UserId, frame.Payload.ToArray(), connection.Aborted);
                    room.Broadcast(message, except: connection);
                }, connection.Aborted);
                break;

            case MessageType.Awareness:
                var entries = AwarenessEntry.Parse(frame.Payload);
                await WithLockAsync(RequireSubscribed(connection, frame).Room, room =>
                {
                    room.Awareness.Apply(entries, connection);
                    room.Broadcast(message, except: connection);
                    return Task.CompletedTask;
                }, connection.Aborted);
                break;
        }
    }

    /// <summary>
    /// Registering, loading and queueing the sync happen under the page lock, and so does every store-and-relay. A new
    /// subscriber therefore gets each update exactly once: in the sync if it was stored before, live if after. The wait
    /// for the sidecar is under the lock on purpose, and bounded by its diff timeout.
    /// </summary>
    private async Task SubscribeAsync(SyncConnection connection, Guid pageId, ReadOnlyMemory<byte> clientStateVector)
    {
        if (connection.Pages.ContainsKey(pageId))
        {
            return;
        }

        if (connection.Pages.Count >= options.MaxPagesPerConnection)
        {
            connection.Enqueue(Frame.EncodeError(pageId, SyncErrors.TooManyPages));
            return;
        }

        var role = await GetRoleAsync(connection.UserId, pageId, connection.Aborted);
        if (role == WorkspaceRole.None)
        {
            connection.Enqueue(Frame.EncodeError(pageId, SyncErrors.PageNotFound));
            return;
        }

        var room = await EnterRoomAsync(pageId, connection.Aborted);
        try
        {
            room.Subscribers.Add(connection);
            connection.Pages[pageId] = new Subscription(room, role >= WorkspaceRole.Editor);

            var log = (await store.LoadAsync(pageId, connection.Aborted)).Inputs;
            connection.Enqueue(await EncodeSyncAsync(pageId, clientStateVector, log, connection.Aborted));
            if (room.Awareness.EncodeAll() is { } presence)
            {
                connection.Enqueue(Frame.Encode(MessageType.Awareness, pageId, presence));
            }
        }
        finally
        {
            ExitRoom(pageId, room);
        }
    }

    /// <summary>The answer to a subscribe: the diff and the server's state vector, or the stored log when there is no diff.</summary>
    private async Task<byte[]> EncodeSyncAsync(
        Guid pageId, ReadOnlyMemory<byte> clientStateVector, IReadOnlyList<byte[]> log, CancellationToken cancellationToken)
    {
        // A page nobody has written to has nothing to diff.
        if (log.Count == 0)
        {
            return Frame.EncodeSync(pageId, [], log);
        }

        try
        {
            // The codec enforces the diff timeout itself, and kills a sidecar that hangs. This is for a codec that does
            // not, so that the page lock is never held for long.
            using var backstop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            backstop.CancelAfter(sidecarOptions.DiffTimeout * 2);
            var diff = await codec.DiffAsync(clientStateVector, log, backstop.Token);
            return Frame.EncodeSync(pageId, diff.StateVector, [diff.Update]);
        }
        catch (YjsUnavailableException ex)
        {
            // Expected while the sidecar restarts, or when there is no Node.
            logger.LogDebug(ex, "No Yjs diff for page {PageId}; sending the stored updates", pageId);
        }
        catch (YjsRejectedException ex)
        {
            // Either the log holds something Yjs cannot decode (compaction will run into the same), or the client sent a
            // state vector that is not one. Either way the client can still sync from the stored updates.
            logger.LogWarning(ex, "Yjs refused to diff page {PageId}, because of a stored update or the client's state vector; sending the stored updates", pageId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The Yjs codec did not answer a diff for page {PageId} in time; sending the stored updates", pageId);
        }

        return Frame.EncodeSync(pageId, [], log);
    }

    private async Task LeaveAsync(SyncConnection connection, Guid pageId)
    {
        if (!connection.Pages.Remove(pageId, out var subscription))
        {
            return;
        }

        // Not cancellable: the cleanup must run even when the connection was dropped.
        var room = subscription.Room;
        await room.Lock.WaitAsync(CancellationToken.None);
        try
        {
            room.Subscribers.Remove(connection);
            if (room.Awareness.RemoveOwner(connection) is { } removal)
            {
                room.Broadcast(Frame.Encode(MessageType.Awareness, pageId, removal), except: connection);
            }
        }
        finally
        {
            ExitRoom(pageId, room);
        }
    }

    /// <summary>
    /// Takes the lock of the page's room, making the room when the page has none. A room that was closed while this waited
    /// is left for a fresh one. Leave it with <see cref="ExitRoom"/>.
    /// </summary>
    private async Task<PageRoom> EnterRoomAsync(Guid pageId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var room = _rooms.GetOrAdd(pageId, _ => new PageRoom());
            try
            {
                await room.Lock.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The room may have been made for this wait alone: it is forgotten, if empty, once its lock is free.
                _ = ForgetIfEmptyAsync(pageId, room);
                throw;
            }

            if (!room.Closed)
            {
                return room;
            }

            // The last subscriber left while this waited; the next GetOrAdd makes a fresh room.
            room.Lock.Release();
        }
    }

    /// <summary>Releases the room's lock, and forgets the room when nobody is in it: a page without subscribers has none.</summary>
    private void ExitRoom(Guid pageId, PageRoom room)
    {
        if (!room.Closed && room.Subscribers.Count == 0)
        {
            room.Closed = true;
            _rooms.TryRemove(new KeyValuePair<Guid, PageRoom>(pageId, room));
        }

        room.Lock.Release();
    }

    private async Task ForgetIfEmptyAsync(Guid pageId, PageRoom room)
    {
        await room.Lock.WaitAsync(CancellationToken.None);
        ExitRoom(pageId, room);
    }

    private async Task<WorkspaceRole> GetRoleAsync(Guid userId, Guid pageId, CancellationToken cancellationToken)
    {
        // A fresh scope per question: the permission service remembers its answers, and a socket lives for hours.
        await using var scope = scopes.CreateAsyncScope();
        var permissions = scope.ServiceProvider.GetRequiredService<IPermissionService>();
        return await permissions.GetPageRoleAsync(userId, pageId, cancellationToken);
    }

    /// <summary>Closes the socket when its session is gone in some other way than a logout (which closes it at once).</summary>
    private async Task CheckSessionAsync(SyncConnection connection)
    {
        try
        {
            using var timer = new PeriodicTimer(options.SessionCheckInterval, time);
            while (await timer.WaitForNextTickAsync(connection.Aborted))
            {
                if (!await tickets.IsActiveAsync(connection.SessionKey, connection.Aborted))
                {
                    connection.RequestClose(SyncClose.SessionEnded, SyncClose.SessionEndedReason);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection ended.
        }
        catch (DbException ex)
        {
            // Not a reason to drop the socket; the next request that needs the session checks it again.
            logger.LogWarning(ex, "Checking the session of a sync connection failed");
        }
    }

    private static Subscription RequireSubscribed(SyncConnection connection, Frame frame) =>
        connection.Pages.TryGetValue(frame.PageId, out var subscription)
            ? subscription
            : throw new ProtocolException($"{frame.Type} for a page the connection does not receive");

    private static async Task WithLockAsync(PageRoom room, Func<PageRoom, Task> action, CancellationToken cancellationToken)
    {
        await room.Lock.WaitAsync(cancellationToken);
        try
        {
            await action(room);
        }
        finally
        {
            room.Lock.Release();
        }
    }

    private sealed class RateLimitedException : Exception;

    private sealed class MessageTooBigException : Exception;
}
