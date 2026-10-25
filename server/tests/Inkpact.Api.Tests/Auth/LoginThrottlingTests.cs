using System.Net;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class LoginThrottlingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private InkpactFactory _factory = null!;

    public async Task InitializeAsync() => _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: _clock);

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> QueryAsync<T>(Func<InkpactDbContext, Task<T>> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    /// <summary>What a caller can see of an answer: status, body and which headers it has.</summary>
    private sealed record Seen(HttpStatusCode Status, string Body, string? RetryAfter)
    {
        public static async Task<Seen> OfAsync(HttpResponseMessage response) =>
            new(response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null);
    }

    private static async Task<Seen> TryAsync(HttpClient client, string email, string password) =>
        await Seen.OfAsync(await client.LoginAsync(email, password));

    private async Task<HttpClient> InstallAsync()
    {
        await _factory.SignedInClientAsync();
        return _factory.CreateClient();
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_and_the_right_one_still_works()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 4; i++)
        {
            (await TryAsync(client, AuthTestExtensions.Email, "wrong password")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await TryAsync(client, AuthTestExtensions.Email, AuthTestExtensions.Password)).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_fifth_wrong_password_locks_the_address_for_a_minute_even_against_the_right_password()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 5; i++)
        {
            (await TryAsync(client, AuthTestExtensions.Email, "wrong password")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await TryAsync(client, AuthTestExtensions.Email, AuthTestExtensions.Password);

        locked.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        locked.Body.ShouldBe("{\"code\":\"login_locked\"}");
        locked.RetryAfter.ShouldBe("60");
    }

    [Fact]
    public async Task The_wait_counts_down_and_the_lock_ends()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 5; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "wrong password");
        }

        _clock.Advance(TimeSpan.FromSeconds(45));
        (await TryAsync(client, AuthTestExtensions.Email, AuthTestExtensions.Password)).RetryAfter.ShouldBe("15");

        _clock.Advance(TimeSpan.FromSeconds(16));
        (await TryAsync(client, AuthTestExtensions.Email, AuthTestExtensions.Password)).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Every_further_failure_doubles_the_lock_up_to_fifteen_minutes()
    {
        var client = await InstallAsync();
        var seen = new List<string?>();

        for (var failure = 1; failure <= 9; failure++)
        {
            var answer = await TryAsync(client, AuthTestExtensions.Email, "wrong password");
            if (failure >= 5)
            {
                var locked = await TryAsync(client, AuthTestExtensions.Email, "wrong password");
                locked.Status.ShouldBe(HttpStatusCode.TooManyRequests);
                seen.Add(locked.RetryAfter);
                _clock.Advance(TimeSpan.FromSeconds(int.Parse(locked.RetryAfter!) + 1));
            }
            else
            {
                answer.Status.ShouldBe(HttpStatusCode.Unauthorized);
            }
        }

        // 1, 2, 4, 8 and 15 minutes: the doubling stops at the maximum.
        seen.ShouldBe(["60", "120", "240", "480", "900"]);
    }

    [Fact]
    public async Task A_successful_login_starts_the_count_again()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 4; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "wrong password");
        }

        (await TryAsync(client, AuthTestExtensions.Email, AuthTestExtensions.Password)).Status.ShouldBe(HttpStatusCode.NoContent);
        (await QueryAsync(db => db.LoginThrottles.CountAsync())).ShouldBe(0);

        for (var i = 0; i < 4; i++)
        {
            (await TryAsync(client, AuthTestExtensions.Email, "wrong password")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task A_day_without_failures_starts_the_count_again()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 4; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "wrong password");
        }

        _clock.Advance(TimeSpan.FromHours(25));

        // One more failure is the first of a new count, not the fifth of the old one.
        (await TryAsync(client, AuthTestExtensions.Email, "wrong password")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await TryAsync(client, AuthTestExtensions.Email, "wrong password")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await QueryAsync(db => db.LoginThrottles.SingleAsync())).Failures.ShouldBe(2);
    }

    [Theory]
    [InlineData("JOS@EXAMPLE.COM")]
    [InlineData("  jos@example.com  ")]
    public async Task Another_way_of_typing_the_same_address_is_locked_too(string spelling)
    {
        var client = await InstallAsync();
        for (var i = 0; i < 5; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "wrong password");
        }

        (await TryAsync(client, spelling, AuthTestExtensions.Password)).Status.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task One_locked_address_does_not_lock_another()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 5; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "wrong password");
        }

        (await TryAsync(client, "someone.else@example.com", "wrong password")).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_address_without_an_account_behaves_exactly_like_one_with_an_account()
    {
        var client = await InstallAsync();
        const string unknown = "nobody@example.com";

        // Same sequence of answers, step by step: 401 four times, then 429 with the same wait and the same body.
        var existing = new List<Seen>();
        var missing = new List<Seen>();
        for (var step = 0; step < 7; step++)
        {
            existing.Add(await TryAsync(client, AuthTestExtensions.Email, "wrong password"));
            missing.Add(await TryAsync(client, unknown, "wrong password"));
            if (step == 5)
            {
                _clock.Advance(TimeSpan.FromSeconds(61));
            }
        }

        missing.ShouldBe(existing);
        missing.Select(s => s.Status).Distinct().ShouldBe([HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], ignoreOrder: true);
    }

    [Fact]
    public async Task Garbage_that_is_not_an_email_counts_too_and_is_locked_too()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            (await TryAsync(client, "not an email", "whatever")).Status.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await TryAsync(client, "not an email", "whatever")).Status.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_table_keeps_a_hash_and_never_the_address_that_was_typed()
    {
        var client = await InstallAsync();
        await TryAsync(client, "Secret.Person@Example.com", "wrong password");

        var row = await QueryAsync(db => db.LoginThrottles.SingleAsync());

        row.Key.Length.ShouldBe(64);
        row.Key.ShouldBe(LoginThrottling.KeyFor("secret.person@example.com"));
        row.Key.ShouldNotContain("secret", Case.Insensitive);
    }

    [Fact]
    public async Task A_lock_does_not_end_a_session_that_already_exists()
    {
        var signedIn = await _factory.SignedInClientAsync();
        var other = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await TryAsync(other, AuthTestExtensions.Email, "wrong password");
        }

        (await signedIn.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Locked_attempts_are_written_to_the_audit_log_with_the_address_that_was_typed()
    {
        var client = await InstallAsync();
        for (var i = 0; i < 6; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "wrong password");
        }

        var events = await QueryAsync(db => db.AuditEvents.Where(e => e.Type == AuditEventTypes.LoginLocked).ToListAsync());
        events.ShouldHaveSingleItem().Email.ShouldBe(AuthTestExtensions.Email);
    }

    [Fact]
    public async Task Without_a_password_for_the_account_there_is_still_a_count()
    {
        var client = await InstallAsync();
        await QueryAsync(async db => await db.Users.ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, (string?)null)));

        for (var i = 0; i < 5; i++)
        {
            await TryAsync(client, AuthTestExtensions.Email, "");
        }

        (await TryAsync(client, AuthTestExtensions.Email, "")).Status.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}

[Collection(PostgresCollection.Name)]
public class AddressRateLimitTests(PostgresFixture postgres)
{
    private async Task<(InkpactFactory Factory, HttpClient Client)> StartAsync(int perMinute)
    {
        // The proxy address is trusted, so X-Forwarded-For says which address each request really came from.
        var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), remoteIp: "10.1.2.3", perAddressPerMinute: perMinute);
        return (factory, factory.CreateClient());
    }

    private static async Task<HttpResponseMessage> LoginFromAsync(HttpClient client, string address, string email)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email, password = "wrong password" }) };
        request.Headers.Add(CsrfProtection.HeaderName, (await client.GetStateAsync()).CsrfToken);
        request.Headers.Add("X-Forwarded-For", address);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task One_address_may_only_try_so_often_whatever_emails_it_tries()
    {
        var (factory, client) = await StartAsync(perMinute: 3);
        await using var disposeFactory = factory;

        for (var i = 0; i < 3; i++)
        {
            (await LoginFromAsync(client, "203.0.113.5", $"person{i}@example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var refused = await LoginFromAsync(client, "203.0.113.5", "person9@example.com");

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.ReadErrorAsync()).Code.ShouldBe("too_many_requests");
        int.Parse(refused.Headers.GetValues("Retry-After").Single()).ShouldBeInRange(1, 60);
    }

    [Fact]
    public async Task Another_address_is_not_affected()
    {
        var (factory, client) = await StartAsync(perMinute: 2);
        await using var disposeFactory = factory;
        for (var i = 0; i < 3; i++)
        {
            await LoginFromAsync(client, "203.0.113.5", $"person{i}@example.com");
        }

        (await LoginFromAsync(client, "203.0.113.5", "again@example.com")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await LoginFromAsync(client, "203.0.113.6", "again@example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Setup_attempts_count_against_the_same_limit()
    {
        var (factory, client) = await StartAsync(perMinute: 2);
        await using var disposeFactory = factory;

        async Task<HttpStatusCode> SetupAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/setup") { Content = JsonContent.Create(new { setupCode = "WRONG" }) };
            request.Headers.Add(CsrfProtection.HeaderName, (await client.GetStateAsync()).CsrfToken);
            request.Headers.Add("X-Forwarded-For", "203.0.113.5");
            return (await client.SendAsync(request)).StatusCode;
        }

        (await SetupAsync()).ShouldBe(HttpStatusCode.BadRequest);
        (await SetupAsync()).ShouldBe(HttpStatusCode.BadRequest);
        (await SetupAsync()).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Reading_the_state_is_never_limited()
    {
        var (factory, client) = await StartAsync(perMinute: 1);
        await using var disposeFactory = factory;

        for (var i = 0; i < 10; i++)
        {
            (await client.GetAsync("/api/auth/state")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_limited_response_still_carries_the_security_headers()
    {
        var (factory, client) = await StartAsync(perMinute: 1);
        await using var disposeFactory = factory;
        await LoginFromAsync(client, "203.0.113.5", "a@example.com");

        var refused = await LoginFromAsync(client, "203.0.113.5", "b@example.com");

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Headers.GetValues("Content-Security-Policy").ShouldNotBeEmpty();
        refused.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }
}
