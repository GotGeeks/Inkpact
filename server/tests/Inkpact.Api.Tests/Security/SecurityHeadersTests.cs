using System.Net;
using Inkpact.Api.Security;
using Inkpact.Api.Tests.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Inkpact.Api.Tests.Security;

[Collection(PostgresCollection.Name)]
public class SecurityHeadersTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly FakeWebRoot _webRoot = new();
    private InkpactFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), webRoot: _webRoot.Path);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _webRoot.Dispose();
    }

    private static readonly Dictionary<string, string> ExpectedHeaders = new()
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["Referrer-Policy"] = "no-referrer",
        ["Cross-Origin-Opener-Policy"] = "same-origin",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["X-Frame-Options"] = "DENY",
        ["Content-Security-Policy"] = SecurityHeaders.ContentSecurityPolicy,
        ["Permissions-Policy"] = SecurityHeaders.PermissionsPolicy,
    };

    [Theory]
    [InlineData("/")]
    [InlineData("/some/deep/link")]
    [InlineData("/index.html")]
    [InlineData("/assets/app-abc123.js")]
    [InlineData("/robots.txt")]
    [InlineData("/assets/missing.js")]
    [InlineData("/api/health")]
    [InlineData("/api/nope")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        var response = await _client.GetAsync(path);

        foreach (var (name, value) in ExpectedHeaders)
        {
            response.Headers.TryGetValues(name, out var actual).ShouldBeTrue($"{name} is missing on {path}");
            actual.ShouldBe([value], $"{name} on {path}");
        }
    }

    [Fact]
    public async Task The_server_header_is_not_sent()
    {
        var response = await _client.GetAsync("/");

        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public void The_content_security_policy_is_strict()
    {
        // Pinned in full, so loosening it shows up in review. Add to it only with a reason in the PR.
        SecurityHeaders.ContentSecurityPolicy.ShouldBe(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; font-src 'self'; "
            + "connect-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; "
            + "form-action 'self'; frame-ancestors 'none'");
        SecurityHeaders.ContentSecurityPolicy.ShouldNotContain("unsafe-inline");
        SecurityHeaders.ContentSecurityPolicy.ShouldNotContain("unsafe-eval");
        SecurityHeaders.ContentSecurityPolicy.ShouldNotContain("upgrade-insecure-requests");
        SecurityHeaders.ContentSecurityPolicy.ShouldNotContain("http:");
        SecurityHeaders.ContentSecurityPolicy.ShouldNotContain("*");
    }

    [Fact]
    public async Task An_unhandled_exception_still_gets_the_headers()
    {
        // The same order as Program.cs: headers first, then the exception handler, then the failing endpoint.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseSecurityHeaders();
                    app.UseExceptionHandler(errors => errors.Run(context =>
                        context.Response.WriteAsJsonAsync(new { code = "server_error" })));
                    app.Run(_ => throw new InvalidOperationException("boom"));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/anything");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("Content-Security-Policy").ShouldBe([SecurityHeaders.ContentSecurityPolicy]);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        (await response.Content.ReadAsStringAsync()).ShouldContain("server_error");
    }
}
