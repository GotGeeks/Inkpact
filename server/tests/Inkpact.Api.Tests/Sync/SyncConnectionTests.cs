using System.Net.WebSockets;
using Inkpact.Api.Sync;

namespace Inkpact.Api.Tests.Sync;

/// <summary>The outgoing side of a connection, against a socket that sends only when the test lets it.</summary>
public class SyncConnectionTests
{
    private static SyncConnection Connect(FakeSocket socket, SyncOptions? options = null) =>
        new(socket, Guid.NewGuid(), "session", options ?? new SyncOptions(), CancellationToken.None);

    [Fact]
    public async Task Messages_go_out_in_order_and_a_close_comes_after_them()
    {
        var socket = new FakeSocket();
        using var connection = Connect(socket);
        var sending = connection.RunSendLoopAsync();

        connection.Enqueue([1]);
        connection.Enqueue([2]);
        connection.RequestClose(WebSocketCloseStatus.NormalClosure, "bye");
        connection.RequestClose(WebSocketCloseStatus.PolicyViolation, "ignored");
        connection.Enqueue([3]);
        socket.Release(10);
        await sending;

        socket.Sent.ShouldBe(["1", "2", "close 1000 bye"]);
    }

    [Fact]
    public async Task A_client_that_falls_too_many_messages_behind_is_dropped()
    {
        var socket = new FakeSocket();
        using var connection = Connect(socket, new SyncOptions { MaxQueuedMessages = 3 });
        var sending = connection.RunSendLoopAsync();
        connection.Enqueue([0]);
        await socket.WaitUntilBlockedAsync();

        // The first is being sent; three more fit in the queue.
        connection.Enqueue([1]).ShouldBeTrue();
        connection.Enqueue([2]).ShouldBeTrue();
        connection.Enqueue([3]).ShouldBeTrue();
        connection.Aborted.IsCancellationRequested.ShouldBeFalse();
        connection.Enqueue([4]).ShouldBeFalse();

        connection.Aborted.IsCancellationRequested.ShouldBeTrue();
        await sending;
    }

    [Fact]
    public async Task A_client_that_falls_too_many_bytes_behind_is_dropped()
    {
        var socket = new FakeSocket();
        using var connection = Connect(socket, new SyncOptions { MaxQueuedBytes = 100 });
        var sending = connection.RunSendLoopAsync();
        connection.Enqueue([0]);
        await socket.WaitUntilBlockedAsync();

        connection.Enqueue(new byte[60]).ShouldBeTrue();
        connection.Enqueue(new byte[60]).ShouldBeFalse();

        connection.Aborted.IsCancellationRequested.ShouldBeTrue();
        await sending;
    }

    [Fact]
    public async Task A_client_that_keeps_up_is_never_dropped()
    {
        var socket = new FakeSocket();
        using var connection = Connect(socket, new SyncOptions { MaxQueuedMessages = 2 });
        var sending = connection.RunSendLoopAsync();

        for (var i = 0; i < 50; i++)
        {
            connection.Enqueue([(byte)i]).ShouldBeTrue();
            socket.Release(1);
            await socket.WaitUntilSentAsync(i + 1);
        }

        connection.Aborted.IsCancellationRequested.ShouldBeFalse();
        connection.RequestClose(WebSocketCloseStatus.NormalClosure, "");
        socket.Release(1);
        await sending;
    }

    [Fact]
    public async Task A_client_that_does_not_answer_a_close_is_dropped_after_the_timeout()
    {
        var socket = new FakeSocket();
        using var connection = Connect(socket, new SyncOptions { CloseTimeout = TimeSpan.FromMilliseconds(50) });
        var sending = connection.RunSendLoopAsync();

        connection.RequestClose(WebSocketCloseStatus.NormalClosure, "");
        socket.Release(1);
        await sending;

        await Task.Delay(500);
        connection.Aborted.IsCancellationRequested.ShouldBeTrue();
    }

    /// <summary>A socket whose sends wait for <see cref="Release"/>. Only the sending side is used.</summary>
    private sealed class FakeSocket : WebSocket
    {
        private readonly SemaphoreSlim _permits = new(0);
        private readonly List<string> _sent = [];
        private int _waiting;

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public void Release(int count) => _permits.Release(count);

        public async Task WaitUntilBlockedAsync()
        {
            while (Volatile.Read(ref _waiting) == 0)
            {
                await Task.Delay(5);
            }
        }

        public async Task WaitUntilSentAsync(int count)
        {
            while (Sent.Count < count)
            {
                await Task.Delay(1);
            }
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            await SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken);

        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken);
            Record(string.Join(",", buffer.ToArray()));
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken);
            Record($"close {(int)closeStatus} {statusDescription}");
        }

        private async Task WaitAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                await _permits.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        private void Record(string entry)
        {
            lock (_sent)
            {
                _sent.Add(entry);
            }
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
