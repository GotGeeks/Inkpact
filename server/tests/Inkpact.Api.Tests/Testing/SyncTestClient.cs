using System.Net;
using System.Net.WebSockets;
using System.Text;
using Inkpact.Api.Sync;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace Inkpact.Api.Tests.Testing;

/// <summary>
/// What a client receives: a frame, or the end of the connection. A Sync frame has the server's state vector (empty in
/// log mode) and its updates.
/// </summary>
public sealed record Received(
    MessageType? Type,
    Guid PageId,
    byte[] Payload,
    IReadOnlyList<byte[]> Updates,
    string? Code,
    WebSocketCloseStatus? CloseStatus,
    string? CloseReason,
    byte[]? StateVector = null)
{
    public bool IsClose => Type is null;

    public override string ToString() =>
        IsClose ? $"close {(int?)CloseStatus} {CloseReason}" : $"{Type} {PageId} {Code}";
}

/// <summary>A person signed in through the real endpoints, with the cookies to open sync sockets as them.</summary>
public sealed class SyncUser(InkpactFactory factory, HttpClient http, CookieContainer cookies, Guid userId)
{
    public HttpClient Http => http;

    public Guid UserId => userId;

    /// <summary>The cookies a browser on the client's address would send.</summary>
    public string CookieHeader => cookies.GetCookieHeader(http.BaseAddress!);

    /// <param name="factory">The app.</param>
    /// <param name="baseAddress">Where the browser is; https gets the secure cookie.</param>
    public static async Task<SyncUser> SetUpAdministratorAsync(InkpactFactory factory, string baseAddress = "http://localhost")
    {
        var (http, cookies) = CreateHttp(factory, baseAddress);
        (await http.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return new SyncUser(factory, http, cookies, (await http.GetStateAsync()).User!.Id);
    }

    public static async Task<SyncUser> LogInAsync(InkpactFactory factory, string email)
    {
        var (http, cookies) = CreateHttp(factory, "http://localhost");
        (await http.LoginAsync(email)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return new SyncUser(factory, http, cookies, (await http.GetStateAsync()).User!.Id);
    }

    /// <summary>Another browser of the same person: a session of its own.</summary>
    public Task<SyncUser> LogInAgainAsync(string email = AuthTestExtensions.Email) => LogInAsync(factory, email);

    /// <summary>Opens a sync socket as this person, from the app's own origin unless told otherwise.</summary>
    public async Task<SyncTestClient> ConnectAsync(string? origin = "http://localhost")
    {
        var socketClient = factory.Server.CreateWebSocketClient();
        socketClient.ConfigureRequest = request =>
        {
            request.Headers.Cookie = CookieHeader;
            if (origin is not null)
            {
                request.Headers.Origin = origin;
            }
        };
        var socket = await socketClient.ConnectAsync(new Uri("ws://localhost" + SyncEndpoints.Path), CancellationToken.None);
        return new SyncTestClient(socket);
    }

    private static (HttpClient Http, CookieContainer Cookies) CreateHttp(InkpactFactory factory, string baseAddress)
    {
        var cookies = new CookieContainer();
        var http = factory.CreateDefaultClient(new Uri(baseAddress), new CookieContainerHandler(cookies));
        return (http, cookies);
    }
}

/// <summary>The client side of a sync socket, speaking the frames byte by byte, as the web client will.</summary>
public sealed class SyncTestClient(WebSocket socket) : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public WebSocket Socket => socket;

    public Task SendAsync(byte[] message) =>
        socket.SendAsync(message, WebSocketMessageType.Binary, true, CancellationToken.None);

    public Task SubscribeAsync(Guid pageId, byte[]? stateVector = null) =>
        SendAsync(Frame.Encode(MessageType.Subscribe, pageId, stateVector ?? []));

    public Task UnsubscribeAsync(Guid pageId)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)MessageType.Unsubscribe);
        writer.WriteVarString(pageId.ToString("D"));
        return SendAsync(writer.ToArray());
    }

    public Task SendUpdateAsync(Guid pageId, byte[] update) => SendAsync(Frame.Encode(MessageType.Update, pageId, update));

    public Task SendAwarenessAsync(Guid pageId, params AwarenessEntry[] entries) =>
        SendAsync(Frame.Encode(MessageType.Awareness, pageId, AwarenessEntry.Encode(entries)));

    /// <summary>Subscribes and returns the updates of the sync answer.</summary>
    public async Task<IReadOnlyList<byte[]>> JoinAsync(Guid pageId) => (await SyncAsync(pageId)).Updates;

    /// <summary>Subscribes with the given state vector (none: the client has nothing) and returns the whole sync answer.</summary>
    public async Task<Received> SyncAsync(Guid pageId, byte[]? stateVector = null)
    {
        await SubscribeAsync(pageId, stateVector);
        var sync = await ReceiveAsync();
        sync.Type.ShouldBe(MessageType.Sync, sync.ToString());
        sync.PageId.ShouldBe(pageId);
        return sync;
    }

    public async Task<Received> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var cancel = new CancellationTokenSource(timeout ?? Timeout);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancel.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new Received(null, Guid.Empty, [], [], null, socket.CloseStatus, socket.CloseStatusDescription);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Parse(message.ToArray());
            }
        }
    }

    /// <summary>Receives until a message of the given type arrives, skipping others (such as awareness).</summary>
    public async Task<Received> ReceiveAsync(MessageType type)
    {
        while (true)
        {
            var received = await ReceiveAsync();
            if (received.IsClose || received.Type == type)
            {
                return received;
            }
        }
    }

    public async Task<Received> ExpectCloseAsync()
    {
        while (true)
        {
            var received = await ReceiveAsync();
            if (received.IsClose)
            {
                return received;
            }
        }
    }

    /// <summary>Checks that nothing arrives for a while. Use with a ping round trip to be sure nothing was in flight.</summary>
    public async Task ExpectSilenceAsync()
    {
        await SendAsync([(byte)MessageType.Ping]);
        var next = await ReceiveAsync();
        next.Type.ShouldBe(MessageType.Pong, $"expected nothing before the pong, got {next}");
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(Timeout).Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                // The server closed it at the same moment, or is gone already.
            }
        }

        socket.Dispose();
    }

    private static Received Parse(byte[] message)
    {
        if (message is [(byte)MessageType.Pong])
        {
            return new Received(MessageType.Pong, Guid.Empty, [], [], null, null, null);
        }

        var reader = new Lib0Reader(message);
        var type = (MessageType)reader.ReadVarUint();
        var pageId = Guid.Parse(reader.ReadVarString());
        switch (type)
        {
            case MessageType.Sync:
                var stateVector = reader.ReadVarUint8Array().ToArray();
                var count = reader.ReadVarUint();
                var updates = new List<byte[]>();
                for (ulong i = 0; i < count; i++)
                {
                    updates.Add(reader.ReadVarUint8Array().ToArray());
                }

                return new Received(type, pageId, [], updates, null, null, null, stateVector);
            case MessageType.Error:
                return new Received(type, pageId, [], [], reader.ReadVarString(), null, null);
            default:
                return new Received(type, pageId, reader.ReadVarUint8Array().ToArray(), [], null, null, null);
        }
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
