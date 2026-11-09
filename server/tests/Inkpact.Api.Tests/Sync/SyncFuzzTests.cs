using System.Net;
using System.Net.WebSockets;
using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Pages;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Sync;

/// <summary>
/// Whatever bytes a client sends, the hub either answers within the protocol or closes that one socket with a policy
/// violation. It never fails with a server error, never hangs, and stays usable for everyone else.
/// </summary>
public class SyncFuzzTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private SyncUser _jos = null!;
    private Guid _pageId;
    private readonly Dictionary<string, int> _outcomes = [];

    public async Task InitializeAsync()
    {
        // Pooled, because every case opens a socket; and no rate limit, which would only add closes for the wrong reason.
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { Pooling = true, MaxPoolSize = 8 };
        _factory = new InkpactFactory(connection.ConnectionString, settings: new Dictionary<string, string> { ["Sync:MessageBurst"] = "100000" });
        _jos = await SyncUser.SetUpAdministratorAsync(_factory);
        await using var scope = _factory.Services.CreateAsyncScope();
        var workspaceId = await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Workspaces.Select(w => w.Id).SingleAsync();
        var response = await _jos.Http.PostJsonAsync($"/api/workspaces/{workspaceId}/pages", new { title = "Fuzz" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        _pageId = (await response.Content.ReadFromJsonAsync<PageChange>())!.Page.Id;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void Random_messages_never_break_the_hub()
    {
        var config = Config.QuickThrowOnFailure.WithMaxTest(150).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(Messages(), messages => RunAsync(messages).GetAwaiter().GetResult()));

        output.WriteLine(string.Join(Environment.NewLine, _outcomes.Select(o => $"{o.Key,-20} {o.Value}")));
        _outcomes.GetValueOrDefault("closed").ShouldBeGreaterThan(10);
        _outcomes.GetValueOrDefault("answered").ShouldBeGreaterThan(10);

        // And afterwards the page works as it should.
        var hub = _factory.Services.GetRequiredService<SyncHub>();
        hub.OpenConnections.ShouldBe(0);
        hub.OpenPages.ShouldBe(0);
    }

    /// <summary>The case CI found: a ping answered before the socket is closed for what follows it.</summary>
    [Fact]
    public async Task A_ping_before_a_message_that_closes_the_socket_does_not_count_as_the_answer()
    {
        (await RunAsync([[(byte)MessageType.Ping], Frame.Encode(MessageType.Update, _pageId, [])])).ShouldBeTrue();

        _outcomes.ShouldBe(new Dictionary<string, int> { ["closed"] = 1 });
    }

    /// <summary>
    /// The case a full run found: a message closes the socket while the next is still being sent. The pause waits until the
    /// hub has dropped the connection, so the next send meets a closed socket every time, not only when the timing says so.
    /// </summary>
    [Fact]
    public async Task A_message_sent_after_the_hub_closed_the_socket_does_not_count_against_the_hub()
    {
        (await RunAsync([Frame.Encode(MessageType.Update, _pageId, []), [(byte)MessageType.Ping]], waitForCloseAfterFirst: true)).ShouldBeTrue();

        _outcomes.ShouldBe(new Dictionary<string, int> { ["closed"] = 1 });
    }

    private async Task<bool> RunAsync(byte[][] messages, bool waitForCloseAfterFirst = false)
    {
        await using var client = await _jos.ConnectAsync();
        var hub = _factory.Services.GetRequiredService<SyncHub>();

        // Pings in the generated messages are answered too; only the pong to the last one means everything was handled.
        // Sending stops at a socket the hub has closed (the test host throws for a send to it): what the hub said about
        // it is read below.
        var open = true;
        foreach (var message in messages)
        {
            if (!(open = await TrySendAsync(client, message)))
            {
                break;
            }

            if (waitForCloseAfterFirst)
            {
                waitForCloseAfterFirst = false;
                await WaitUntilAsync(() => hub.OpenConnections == 0);
            }
        }

        if (open)
        {
            await TrySendAsync(client, [(byte)MessageType.Ping]);
        }

        var pongsExpected = messages.Count(message => message is [(byte)MessageType.Ping]) + 1;
        while (true)
        {
            var received = await client.ReceiveAsync();
            if (received.IsClose)
            {
                received.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
                received.CloseReason.ShouldBe(SyncClose.ProtocolErrorReason);
                Count("closed");
                break;
            }

            if (received.Type == MessageType.Pong)
            {
                if (--pongsExpected == 0)
                {
                    Count("answered");
                    break;
                }

                continue;
            }

            received.Type.ShouldBeOneOf(MessageType.Sync, MessageType.Error, MessageType.Awareness);
        }

        await client.DisposeAsync();
        await WaitUntilAsync(() => hub.OpenConnections == 0);
        return hub.OpenConnections == 0;
    }

    /// <summary>
    /// Sends, or answers false when the hub has already closed the socket: the test host throws for that before the client
    /// has read the close (so the socket's state still says open). The close itself is read and checked afterwards, so a
    /// send that failed for any other reason still fails the case there.
    /// </summary>
    private static async Task<bool> TrySendAsync(SyncTestClient client, byte[] message)
    {
        try
        {
            await client.SendAsync(message);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + SyncTestClient.Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }

    private void Count(string outcome) => _outcomes[outcome] = _outcomes.GetValueOrDefault(outcome) + 1;

    /// <summary>
    /// A few messages per socket. Most are valid (subscribe, update, awareness, unsubscribe, ping on the real page, so
    /// that the hub does real work in between); the rest are noise, or a real type and page id followed by noise, so
    /// that the parser gets past its first checks.
    /// </summary>
    private Arbitrary<byte[][]> Messages()
    {
        var bytes = Gen.Choose(0, 40).SelectMany(length => Gen.ArrayOf(Gen.Choose(0, 255).Select(b => (byte)b), length));
        var pageId = new Lib0Writer();
        pageId.WriteVarString(_pageId.ToString("D"));
        var idBytes = pageId.ToArray();

        var valid = Gen.OneOf(
            bytes.Select(stateVector => Frame.Encode(MessageType.Subscribe, _pageId, stateVector)),
            bytes.Select(update => Frame.Encode(MessageType.Update, _pageId, update)),
            Gen.Choose(0, 1000).Select(clock => Frame.Encode(
                MessageType.Awareness, _pageId, AwarenessEntry.Encode([new AwarenessEntry(1, (ulong)clock, "{}")]))),
            Gen.Constant((byte[])[(byte)MessageType.Unsubscribe, .. idBytes]),
            Gen.Constant((byte[])[(byte)MessageType.Ping]));
        var noise = Gen.OneOf(
            bytes,
            Gen.Choose(0, 9).SelectMany(type => bytes.Select(tail => (byte[])[(byte)type, .. idBytes, .. tail])));

        var message = Gen.OneOf(valid, valid, valid, valid, noise);
        return Arb.From(Gen.Choose(1, 5).SelectMany(count => Gen.ArrayOf(message, count)));
    }
}
