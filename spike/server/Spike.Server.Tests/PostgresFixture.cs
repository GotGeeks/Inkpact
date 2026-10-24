using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Spike.Server.Tests;

/// <summary>One Postgres container and one running server for all tests; tests isolate by page id.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public WebApplication App { get; private set; } = null!;

    public Uri WebSocketUri { get; private set; } = null!;

    public IUpdateStore Store => App.Services.GetRequiredService<IUpdateStore>();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        App = await SpikeApp.BuildAsync(
        [
            "--urls", "http://127.0.0.1:0",
            "--ConnectionStrings:Spike", _postgres.GetConnectionString(),
        ]);
        await App.StartAsync();

        var address = App.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        WebSocketUri = new Uri(address.Replace("http://", "ws://") + "/ws");
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public static string NewPageId() => "page-" + Guid.NewGuid().ToString("N");
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
