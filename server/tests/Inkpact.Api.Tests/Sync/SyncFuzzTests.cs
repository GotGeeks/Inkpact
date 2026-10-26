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
[Collection(PostgresCollection.Name)]
public class SyncFuzzTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
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

    private async Task<bool> RunAsync(byte[][] messages)
    {
        await using var client = await _jos.ConnectAsync();
        foreach (var message in messages)
        {
            await client.SendAsync(message);
        }

        await client.SendAsync([(byte)MessageType.Ping]);
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
                Count("answered");
                break;
            }

            received.Type.ShouldBeOneOf(MessageType.Sync, MessageType.Error, MessageType.Awareness);
        }

        await client.DisposeAsync();
        var hub = _factory.Services.GetRequiredService<SyncHub>();
        var deadline = DateTime.UtcNow + SyncTestClient.Timeout;
        while (hub.OpenConnections > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        return hub.OpenConnections == 0;
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
