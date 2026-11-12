using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Crdt;
using Inkpact.Api.Health;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests;

public class HealthEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
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
    public async Task Ready_without_the_sidecar_is_degraded_and_still_answers_200_with_three_words_and_nothing_else()
    {
        // The factory runs without the Yjs sidecar unless a test asks for it.
        var response = await _client.GetAsync("/api/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe("{\"status\":\"degraded\",\"checks\":{\"database\":\"ok\",\"sidecar\":\"unavailable\"}}");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Ready_with_the_database_and_the_sidecar_is_ok()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), sidecar: true);
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);

        var response = await client.GetAsync("/api/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe("{\"status\":\"ok\",\"checks\":{\"database\":\"ok\",\"sidecar\":\"ok\"}}");
    }

    // A database that is gone is met in two ways: a kept connection that breaks, or (none kept) a new one that is
    // refused. Both must read as unavailable; the second once read as a migration that waits.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_is_unhealthy_once_the_database_is_gone_but_only_after_the_cached_answer_has_aged(bool noConnectionKept)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = new InkpactFactory(connectionString, time: clock);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await DropDatabaseAsync(connectionString);
        if (noConnectionKept)
        {
            await using var sameString = new Npgsql.NpgsqlConnection(connectionString);
            Npgsql.NpgsqlConnection.ClearPool(sameString);
        }

        // Within the five seconds the answer is the one already made: the database is not asked again.
        clock.Advance(Readiness.CacheFor - TimeSpan.FromSeconds(1));
        (await client.GetAsync("/api/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromSeconds(2));
        var response = await client.GetAsync("/api/health/ready");
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe("{\"status\":\"unhealthy\",\"checks\":{\"database\":\"unavailable\",\"sidecar\":\"unavailable\"}}");

        // The plain health check reads nothing, so it still answers.
        (await client.GetAsync("/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ready_is_unhealthy_while_a_migration_waits()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            var newest = (await db.Database.GetAppliedMigrationsAsync()).Last();
            await db.Database.ExecuteSqlAsync($"delete from \"__EFMigrationsHistory\" where \"MigrationId\" = {newest}");
        }

        var response = await _client.GetAsync("/api/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<ReadinessReport>())!.Checks.Database.ShouldBe(ReadinessReport.MigrationPending);
    }

    private async Task DropDatabaseAsync(string connectionString)
    {
        var name = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var admin = new Npgsql.NpgsqlConnection(postgres.Container.GetConnectionString());
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"drop database \"{name}\" with (force)";
        await command.ExecuteNonQueryAsync();
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
