using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class AuthEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        _factory = new InkpactFactory(_connectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> QueryAsync<T>(Func<InkpactDbContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    // ---- state ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_install_asks_for_setup()
    {
        var state = await _factory.CreateClient().GetStateAsync();

        state.SetupRequired.ShouldBeTrue();
        state.User.ShouldBeNull();
    }

    [Fact]
    public async Task After_setup_the_state_has_the_signed_in_user_and_no_setup()
    {
        var client = await _factory.SignedInClientAsync();

        var state = await client.GetStateAsync();

        state.SetupRequired.ShouldBeFalse();
        state.User.ShouldNotBeNull();
        state.User.Email.ShouldBe(AuthTestExtensions.Email);
        state.User.DisplayName.ShouldBe("Jos");
        state.User.IsAdmin.ShouldBeTrue();
    }

    [Fact]
    public async Task A_visitor_after_setup_sees_neither_setup_nor_a_user()
    {
        await _factory.SignedInClientAsync();

        var state = await _factory.CreateClient().GetStateAsync();

        state.SetupRequired.ShouldBeFalse();
        state.User.ShouldBeNull();
    }

    // ---- setup ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Setup_creates_an_administrator_with_a_private_workspace_and_signs_in()
    {
        var client = _factory.CreateClient();

        var response = await client.SetupAsync(workspaceName: "Jos's notes");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.SetCookies().ShouldContain(c => c.StartsWith("inkpact="));

        var (user, workspace) = await QueryAsync(async db => (await db.Users.SingleAsync(), await db.Workspaces.SingleAsync()));
        user.Email.ShouldBe(AuthTestExtensions.Email);
        user.IsAdmin.ShouldBeTrue();
        user.PasswordHash.ShouldStartWith("$argon2id$");
        user.PasswordHash.ShouldNotContain(AuthTestExtensions.Password);
        workspace.Name.ShouldBe("Jos's notes");
        workspace.OwnerId.ShouldBe(user.Id);

        (await client.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Setup_stores_the_email_lower_case_but_keeps_what_was_typed()
    {
        await _factory.CreateClient().SetupAsync(email: "  Jos@Example.com ");

        var user = await QueryAsync(db => db.Users.SingleAsync());
        user.Email.ShouldBe("jos@example.com");
        user.NormalizedEmail.ShouldBe("jos@example.com");
    }

    [Fact]
    public async Task Setup_with_the_wrong_code_is_refused_and_creates_nothing()
    {
        var response = await _factory.CreateClient().SetupAsync(setupCode: "WRONGCODE");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("setup_code_invalid", "setupCode"));
        (await QueryAsync(db => db.Users.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task The_setup_code_is_not_case_sensitive()
    {
        var response = await _factory.CreateClient().SetupAsync(setupCode: " testcode ");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Setup_is_closed_once_an_account_exists()
    {
        await _factory.SignedInClientAsync();

        var response = await _factory.CreateClient().SetupAsync(email: "other@example.com");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadErrorAsync()).Code.ShouldBe("setup_not_available");
        (await QueryAsync(db => db.Users.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task Setup_does_not_leak_whether_the_code_was_right_once_it_is_closed()
    {
        await _factory.SignedInClientAsync();

        var wrongCode = await _factory.CreateClient().SetupAsync(setupCode: "WRONGCODE");

        wrongCode.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Two_setups_at_the_same_moment_make_exactly_one_administrator()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            _factory.CreateClient().SetupAsync(email: $"admin{i}@example.com")));

        results.Select(r => r.StatusCode).Order().ShouldBe(
            [HttpStatusCode.NoContent, HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.Conflict]);
        (await QueryAsync(db => db.Users.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.Workspaces.CountAsync())).ShouldBe(1);
    }

    [Theory]
    [InlineData("not-an-email", "Jos", "Private", "correct horse battery", "email_invalid", "email")]
    [InlineData("", "Jos", "Private", "correct horse battery", "email_invalid", "email")]
    [InlineData("jos@example.com", "", "Private", "correct horse battery", "display_name_invalid", "displayName")]
    [InlineData("jos@example.com", "   ", "Private", "correct horse battery", "display_name_invalid", "displayName")]
    [InlineData("jos@example.com", "Jos", "", "correct horse battery", "workspace_name_invalid", "workspaceName")]
    [InlineData("jos@example.com", "Jos", "Private", "short", "password_too_short", "password")]
    [InlineData("jos@example.com", "Jos", "Private", "jos@example.com", "password_same_as_email", "password")]
    public async Task Setup_validates_every_field(
        string email, string displayName, string workspaceName, string password, string code, string field)
    {
        var response = await _factory.CreateClient().SetupAsync(email, password, displayName, workspaceName);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError(code, field));
        (await QueryAsync(db => db.Users.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Setup_refuses_a_name_that_is_too_long()
    {
        var response = await _factory.CreateClient().SetupAsync(displayName: new string('x', 201));

        (await response.ReadErrorAsync()).Code.ShouldBe("display_name_invalid");
    }

    [Fact]
    public async Task Setup_with_missing_fields_is_a_validation_error_not_a_crash()
    {
        var response = await _factory.CreateClient().PostJsonAsync("/api/auth/setup", new { setupCode = InkpactFactory.SetupCode });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---- login ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_with_the_right_password_signs_in()
    {
        await _factory.SignedInClientAsync();
        var client = _factory.CreateClient();

        var response = await client.LoginAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetStateAsync()).User!.Email.ShouldBe(AuthTestExtensions.Email);
    }

    [Theory]
    [InlineData("JOS@EXAMPLE.COM")]
    [InlineData("  jos@example.com  ")]
    public async Task Login_ignores_case_and_surrounding_spaces_in_the_email(string email)
    {
        await _factory.SignedInClientAsync();

        var response = await _factory.CreateClient().LoginAsync(email);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_email_get_the_same_answer()
    {
        await _factory.SignedInClientAsync();
        var client = _factory.CreateClient();

        var wrongPassword = await client.LoginAsync(password: "not the password");
        var unknownEmail = await client.LoginAsync(email: "nobody@example.com");

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var wrongBody = await wrongPassword.Content.ReadAsStringAsync();
        var unknownBody = await unknownEmail.Content.ReadAsStringAsync();
        wrongBody.ShouldBe(unknownBody);
        (await wrongPassword.ReadErrorAsync()).ShouldBe(new ApiError("invalid_credentials"));
        wrongPassword.SetCookies().ShouldBeEmpty();
        unknownEmail.SetCookies().ShouldBeEmpty();
        (await client.GetStateAsync()).User.ShouldBeNull();
    }

    [Fact]
    public async Task Login_without_fields_is_refused_like_any_wrong_login()
    {
        var response = await _factory.CreateClient().PostJsonAsync("/api/auth/login", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadErrorAsync()).Code.ShouldBe("invalid_credentials");
    }

    [Fact]
    public async Task An_account_without_a_password_cannot_log_in_with_an_empty_one()
    {
        await _factory.SignedInClientAsync();
        await QueryAsync(async db =>
        {
            await db.Users.ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, (string?)null));
            return 0;
        });

        var response = await _factory.CreateClient().LoginAsync(password: "");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_upgrades_a_hash_made_with_weaker_parameters()
    {
        await _factory.SignedInClientAsync();
        Span<char> weak = stackalloc char[Geralt.Argon2id.HashSize];
        Geralt.Argon2id.ComputeHash(weak, System.Text.Encoding.UTF8.GetBytes(AuthTestExtensions.Password), 1, Geralt.Argon2id.MinMemorySize);
        var weakHash = weak[..weak.IndexOf('\0')].ToString();
        await QueryAsync(async db =>
        {
            await db.Users.ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, weakHash));
            return 0;
        });

        (await _factory.CreateClient().LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var upgraded = (await QueryAsync(db => db.Users.SingleAsync())).PasswordHash!;
        upgraded.ShouldNotBe(weakHash);
        upgraded.ShouldStartWith("$argon2id$v=19$m=47104,t=1,p=1$");
    }

    // ---- logout and sessions -------------------------------------------------------------------------------

    [Fact]
    public async Task Logout_ends_this_session_only()
    {
        await _factory.SignedInClientAsync();
        var first = _factory.CreateClient();
        var second = _factory.CreateClient();
        await first.LoginAsync();
        await second.LoginAsync();

        var response = await first.PostJsonAsync("/api/auth/logout");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await first.GetStateAsync()).User.ShouldBeNull();
        (await second.GetStateAsync()).User.ShouldNotBeNull();
        (await QueryAsync(db => db.Sessions.CountAsync())).ShouldBe(2); // the setup session and the second login
    }

    [Fact]
    public async Task A_cookie_from_before_logout_does_not_work_again()
    {
        var client = await _factory.SignedInClientAsync();
        var cookie = (await client.LoginAsync()).SetCookies().Single(c => c.StartsWith("inkpact=")).Split(';')[0];

        await client.PostJsonAsync("/api/auth/logout");

        var replay = _factory.CreateClient();
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        (await replay.GetStateAsync()).User.ShouldBeNull();
    }

    [Fact]
    public async Task Logout_everywhere_ends_every_session_of_the_user()
    {
        var setup = await _factory.SignedInClientAsync();
        var laptop = _factory.CreateClient();
        var phone = _factory.CreateClient();
        await laptop.LoginAsync();
        await phone.LoginAsync();

        var response = await laptop.PostJsonAsync("/api/auth/logout-everywhere");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await laptop.GetStateAsync()).User.ShouldBeNull();
        (await phone.GetStateAsync()).User.ShouldBeNull();
        (await setup.GetStateAsync()).User.ShouldBeNull();
        (await QueryAsync(db => db.Sessions.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Deleting_a_session_row_signs_that_browser_out_on_its_next_request()
    {
        var client = await _factory.SignedInClientAsync();
        (await client.GetStateAsync()).User.ShouldNotBeNull();

        await QueryAsync(async db => await db.Sessions.ExecuteDeleteAsync());

        (await client.GetStateAsync()).User.ShouldBeNull();
    }

    [Fact]
    public async Task Removing_the_user_signs_them_out_at_once()
    {
        var client = await _factory.SignedInClientAsync();
        await QueryAsync(async db =>
        {
            await db.Workspaces.ExecuteDeleteAsync();
            return await db.Users.ExecuteDeleteAsync();
        });

        (await client.GetStateAsync()).User.ShouldBeNull();
        (await client.PostJsonAsync("/api/auth/logout")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_records_the_browser()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Test) Inkpact/1");
        await client.SetupAsync();

        (await QueryAsync(db => db.Sessions.SingleAsync())).UserAgent.ShouldBe("Mozilla/5.0 (Test) Inkpact/1");
    }

    [Fact]
    public async Task A_forged_cookie_does_not_sign_in()
    {
        await _factory.SignedInClientAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "inkpact=CfDJ8-this-is-not-a-real-protected-value");

        (await client.GetStateAsync()).User.ShouldBeNull();
    }

    // ---- the API as a whole --------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/logout-everywhere")]
    public async Task Signing_out_needs_a_session_and_answers_json_instead_of_a_redirect(string path)
    {
        var response = await _factory.CreateClient().PostJsonAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("not_authenticated"));
    }

    [Theory]
    [InlineData("/api/auth/setup")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/logout")]
    public async Task Changing_state_needs_json(string path)
    {
        var client = await _factory.SignedInClientAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "a@example.com" });

        var response = await client.PostAsync(path, form);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await response.ReadErrorAsync()).ShouldBe(new ApiError("unsupported_media_type"));
    }

    [Fact]
    public async Task A_cross_site_form_post_cannot_log_anyone_out()
    {
        var client = await _factory.SignedInClientAsync();

        var response = await client.PostAsync(
            "/api/auth/logout-everywhere",
            new StringContent("x=1", System.Text.Encoding.UTF8, "text/plain"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await client.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Reading_state_does_not_need_json()
    {
        (await _factory.CreateClient().GetAsync("/api/auth/state")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_endpoint_without_its_own_policy_is_denied_by_default()
    {
        // Not a real API path: the fallback for unknown /api paths answers anonymously with 404, so the proof that
        // the fallback policy is wired is the logout endpoint above and the policy itself.
        await using var scope = _factory.Services.CreateAsyncScope();
        var policy = await scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>()
            .GetFallbackPolicyAsync();

        policy.ShouldNotBeNull();
        policy.Requirements.ShouldContain(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task Auth_responses_carry_the_security_headers()
    {
        var response = await _factory.CreateClient().LoginAsync();

        response.Headers.GetValues("Content-Security-Policy").ShouldNotBeEmpty();
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }

    [Fact]
    public async Task Auth_endpoints_are_not_cacheable()
    {
        var response = await _factory.CreateClient().GetAsync("/api/auth/state");

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
