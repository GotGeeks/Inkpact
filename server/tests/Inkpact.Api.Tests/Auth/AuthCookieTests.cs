using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Tests.Testing;

namespace Inkpact.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class AuthCookieTests(PostgresFixture postgres)
{
    private async Task<(InkpactFactory Factory, HttpClient Client)> StartAsync(
        string? cookieSecure = null, string[]? trustedProxies = null, string? remoteIp = null, bool https = false)
    {
        var factory = new InkpactFactory(
            await postgres.CreateDatabaseAsync(), cookieSecure: cookieSecure, trustedProxies: trustedProxies, remoteIp: remoteIp);
        return (factory, https ? factory.CreateHttpsClient() : factory.CreateClient());
    }

    private static async Task<string> SessionCookieAsync(HttpClient client, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup")
        {
            Content = JsonContent.Create(new
            {
                setupCode = InkpactFactory.SetupCode,
                email = AuthTestExtensions.Email,
                displayName = "Jos",
                password = AuthTestExtensions.Password,
                workspaceName = "Private",
            }),
        };
        configure?.Invoke(request);
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return response.SetCookies().Single();
    }

    [Fact]
    public async Task Over_https_the_cookie_is_host_prefixed_secure_http_only_and_lax()
    {
        var (factory, client) = await StartAsync(https: true);
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client);

        cookie.ShouldStartWith("__Host-inkpact=");
        var attributes = cookie.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        attributes.ShouldContain("secure");
        attributes.ShouldContain("httponly");
        attributes.ShouldContain("samesite=lax");
        attributes.ShouldContain("path=/");
        attributes.ShouldNotContain(a => a.StartsWith("domain"));
    }

    [Fact]
    public async Task Over_plain_http_the_cookie_has_a_plain_name_and_no_secure_flag()
    {
        var (factory, client) = await StartAsync();
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client);

        cookie.ShouldStartWith("inkpact=");
        cookie.ToLowerInvariant().ShouldNotContain("secure");
        cookie.ToLowerInvariant().ShouldContain("httponly");
        cookie.ToLowerInvariant().ShouldContain("samesite=lax");
        cookie.ToLowerInvariant().ShouldNotContain("domain");
    }

    [Fact]
    public async Task The_cookie_value_is_not_the_user_id_or_anything_readable()
    {
        var (factory, client) = await StartAsync();
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client);
        var value = cookie.Split(';')[0].Split('=', 2)[1];

        value.Length.ShouldBeGreaterThan(40);
        value.ShouldNotContain(AuthTestExtensions.Email);
        Guid.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_cookie_is_a_session_cookie_that_outlives_the_browser_session()
    {
        var (factory, client) = await StartAsync();
        await using var disposeFactory = factory;

        var cookie = (await SessionCookieAsync(client)).ToLowerInvariant();

        cookie.ShouldContain("expires=");
    }

    [Fact]
    public async Task An_https_session_works_on_later_requests_over_https()
    {
        var (factory, client) = await StartAsync(https: true);
        await using var disposeFactory = factory;
        await SessionCookieAsync(client);

        (await client.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Forced_secure_sets_the_flag_even_on_plain_http()
    {
        var (factory, client) = await StartAsync(cookieSecure: "true");
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client);

        cookie.ToLowerInvariant().ShouldContain("secure");
    }

    [Fact]
    public async Task Never_secure_gives_a_plain_cookie_even_over_https()
    {
        var (factory, client) = await StartAsync(cookieSecure: "false", https: true);
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client);

        // The "__Host-" prefix would make browsers refuse a cookie without Secure, so it is not used.
        cookie.ShouldStartWith("inkpact=");
        cookie.ToLowerInvariant().ShouldNotContain("secure");
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("172.18.0.2", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("203.0.113.9", false)]
    public async Task X_forwarded_proto_counts_only_from_a_trusted_proxy(string remoteIp, bool trusted)
    {
        var (factory, client) = await StartAsync(remoteIp: remoteIp);
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"));

        cookie.StartsWith(trusted ? "__Host-inkpact=" : "inkpact=").ShouldBeTrue(cookie);
    }

    [Fact]
    public async Task The_trusted_ranges_can_be_set()
    {
        var (factory, client) = await StartAsync(trustedProxies: ["203.0.113.0/24"], remoteIp: "203.0.113.9");
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"));

        cookie.ShouldStartWith("__Host-inkpact=");
    }

    [Fact]
    public async Task A_private_address_is_no_longer_trusted_when_the_ranges_are_set_elsewhere()
    {
        var (factory, client) = await StartAsync(trustedProxies: ["203.0.113.0/24"], remoteIp: "10.1.2.3");
        await using var disposeFactory = factory;

        var cookie = await SessionCookieAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"));

        cookie.ShouldStartWith("inkpact=");
    }

    [Fact]
    public async Task Behind_a_proxy_a_later_request_with_the_prefixed_cookie_is_signed_in()
    {
        var (factory, client) = await StartAsync(remoteIp: "10.1.2.3");
        await using var disposeFactory = factory;
        var cookie = (await SessionCookieAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"))).Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/state");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Cookie", cookie);
        var response = await new HttpClient(new NoCookiesHandler(factory)) { BaseAddress = client.BaseAddress }.SendAsync(request);

        var state = await response.Content.ReadFromJsonAsync<Inkpact.Api.Auth.AuthState>();
        state!.User.ShouldNotBeNull();
    }

    /// <summary>The factory's handler without its cookie container, so the test sets the Cookie header itself.</summary>
    private sealed class NoCookiesHandler(InkpactFactory factory) : DelegatingHandler(factory.Server.CreateHandler());
}
