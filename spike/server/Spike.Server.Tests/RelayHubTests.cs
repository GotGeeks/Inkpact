using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Spike.Server.Tests;

public sealed class RelayHubTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private WebApplication _app = null!;
    private Uri _baseUri = null!;

    public async Task InitializeAsync()
    {
        _app = SpikeApp.Build(["--urls", "http://127.0.0.1:0"]);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _baseUri = new Uri(address.Replace("http://", "ws://"));
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Message_reaches_other_client_on_same_page_but_is_not_echoed()
    {
        using var alice = await ConnectAsync("page-a");
        using var bob = await ConnectAsync("page-a");

        await SendAsync(alice, [1, 2, 3]);
        Assert.Equal([1, 2, 3], await ReceiveAsync(bob));

        // An echo of the first message would be queued for the sender before this reply.
        await SendAsync(bob, [4]);
        Assert.Equal([4], await ReceiveAsync(alice));
    }

    [Fact]
    public async Task Message_does_not_reach_client_on_other_page()
    {
        using var alice = await ConnectAsync("page-a");
        using var carol = await ConnectAsync("page-b");

        await SendAsync(alice, [1]);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => carol.ReceiveAsync(new byte[16], cts.Token));
    }

    [Fact]
    public async Task Large_message_split_over_frames_arrives_whole()
    {
        using var alice = await ConnectAsync("page-a");
        using var bob = await ConnectAsync("page-a");
        var payload = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();

        await SendAsync(alice, payload);

        Assert.Equal(payload, await ReceiveAsync(bob));
    }

    [Fact]
    public async Task Request_without_page_is_rejected()
    {
        using var client = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(
            () => client.ConnectAsync(new Uri(_baseUri, "/ws"), CancellationToken.None));
    }

    private async Task<ClientWebSocket> ConnectAsync(string pageId)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(_baseUri, $"/ws?page={pageId}"), CancellationToken.None);
        return client;
    }

    private static async Task SendAsync(ClientWebSocket socket, byte[] message)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await socket.SendAsync(message, WebSocketMessageType.Binary, true, cts.Token);
    }

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cts.Token);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return message.ToArray();
    }
}
