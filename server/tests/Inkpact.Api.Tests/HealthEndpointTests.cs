using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inkpact.Api.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

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
}
