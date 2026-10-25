using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests;

[Collection(PostgresCollection.Name)]
public class HealthEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync());
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/api/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body.ShouldNotBeNull();
        body["status"].ShouldBe("ok");
    }

    [Fact]
    public async Task Unknown_route_returns_not_found()
    {
        var response = await _client.GetAsync("/api/nope");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Starting_the_app_migrates_the_database()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();

        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await db.Database.GetAppliedMigrationsAsync()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_app_refuses_to_start_without_a_connection_string()
    {
        await using var factory = new InkpactFactory(connectionString: "");

        var exception = Should.Throw<InvalidOperationException>(() => factory.CreateClient());
        exception.Message.ShouldContain("ConnectionStrings:Inkpact");
    }
}
