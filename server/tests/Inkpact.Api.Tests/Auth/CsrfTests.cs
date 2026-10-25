using System.Net;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class CsrfTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;

    public async Task InitializeAsync() => _factory = new InkpactFactory(await postgres.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static HttpRequestMessage Post(string path, object body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Add(CsrfProtection.HeaderName, token);
        }

        return request;
    }

    private static object LoginBody => new { email = AuthTestExtensions.Email, password = AuthTestExtensions.Password };

    [Fact]
    public async Task The_state_hands_out_a_token_and_its_cookie()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/state");

        var state = await response.Content.ReadFromJsonAsync<AuthState>();
        state!.CsrfToken.ShouldNotBeNullOrWhiteSpace();
        var cookie = response.SetCookies().Single(c => c.StartsWith("inkpact-csrf="));
        cookie.ToLowerInvariant().ShouldContain("httponly");
        cookie.ToLowerInvariant().ShouldContain("samesite=strict");
        cookie.ToLowerInvariant().ShouldContain("path=/");
    }

    [Fact]
    public async Task The_state_is_not_cacheable_and_cannot_be_framed()
    {
        var response = await _factory.CreateClient().GetAsync("/api/auth/state");

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/setup")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/logout-everywhere")]
    [InlineData("/api/anything/else")]
    public async Task A_state_change_without_a_token_is_refused(string path)
    {
        var client = _factory.CreateClient();
        await client.GetStateAsync(); // the cookie is there, the header is not

        var response = await client.SendAsync(Post(path, LoginBody, token: null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("csrf_invalid"));
    }

    [Fact]
    public async Task A_state_change_without_the_cookie_is_refused_even_with_a_token()
    {
        var token = (await _factory.CreateClient().GetStateAsync()).CsrfToken;
        var stranger = _factory.CreateClient(); // another browser: no cookie

        var response = await stranger.SendAsync(Post("/api/auth/login", LoginBody, token));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("csrf_invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("CfDJ8-looks-real-but-is-not-0123456789abcdef")]
    public async Task A_made_up_token_is_refused(string token)
    {
        var client = _factory.CreateClient();
        await client.GetStateAsync();

        var response = await client.SendAsync(Post("/api/auth/login", LoginBody, token));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_refused_setup_creates_no_account()
    {
        var client = _factory.CreateClient();
        await client.GetStateAsync();

        await client.SendAsync(Post("/api/auth/setup", new { setupCode = InkpactFactory.SetupCode, email = AuthTestExtensions.Email, displayName = "Jos", password = AuthTestExtensions.Password, workspaceName = "Private" }, token: null));

        await using var scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Users.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_valid_token_is_accepted_and_may_be_used_again()
    {
        var client = _factory.CreateClient();
        var token = (await client.GetStateAsync()).CsrfToken;

        (await client.SendAsync(Post("/api/auth/setup", new { setupCode = InkpactFactory.SetupCode, email = AuthTestExtensions.Email, displayName = "Jos", password = AuthTestExtensions.Password, workspaceName = "Private" }, token)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Setup signed this browser in, so the token it had before no longer matches who it is.
        var fresh = (await client.GetStateAsync()).CsrfToken;
        (await client.SendAsync(Post("/api/auth/logout", new { }, fresh))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_token_from_before_the_login_stops_working_after_it()
    {
        await _factory.SignedInClientAsync();
        var client = _factory.CreateClient();
        var beforeLogin = (await client.GetStateAsync()).CsrfToken;

        (await client.SendAsync(Post("/api/auth/login", LoginBody, beforeLogin))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var stale = await client.SendAsync(Post("/api/auth/logout-everywhere", new { }, beforeLogin));
        stale.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stale.ReadErrorAsync()).Code.ShouldBe("csrf_invalid");
        (await client.GetStateAsync()).User.ShouldNotBeNull(); // nothing was signed out
    }

    [Fact]
    public async Task One_users_token_does_not_work_for_another_browser_signed_in_as_someone_else()
    {
        var admin = await _factory.SignedInClientAsync();
        var adminToken = (await admin.GetStateAsync()).CsrfToken;
        var anonymous = _factory.CreateClient();
        await anonymous.GetStateAsync();

        var response = await anonymous.SendAsync(Post("/api/auth/login", LoginBody, adminToken));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reading_never_needs_a_token()
    {
        var client = await _factory.SignedInClientAsync();

        (await client.GetAsync("/api/auth/state")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/workspaces")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/does-not-exist")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_form_post_is_still_refused_as_not_json_before_the_token_is_looked_at()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "a@b.c" }));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Without_a_token_the_password_is_never_looked_at()
    {
        var client = _factory.CreateClient();
        await client.GetStateAsync();

        // Without a token the password is never looked at, so no failure is counted and nothing is audited.
        for (var i = 0; i < 8; i++)
        {
            (await client.SendAsync(Post("/api/auth/login", LoginBody, token: null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
        (await db.LoginThrottles.CountAsync()).ShouldBe(0);
        (await db.AuditEvents.CountAsync()).ShouldBe(0);
    }
}
