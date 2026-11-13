using System.Net;
using System.Net.Http.Json;
using Inkpact.Api.Account;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Account;

/// <summary>
/// A person's own account (run B.9): the display name, and the browsers they are signed in on, each signed out from
/// another. With real sessions in Postgres; the sockets that close with them are in SyncHubTests.
/// </summary>
public class AccountEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private const string ChromeOnWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private const string SafariOnIPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Mobile/15E148 Safari/604.1";

    private readonly MutableTimeProvider _clock = new(Start);
    private InkpactFactory _factory = null!;
    private HttpClient _jos = null!;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: _clock);
        _jos = _factory.CreateClient();
        _jos.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeOnWindows);
        (await _jos.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    /// <summary>Another browser, signed in as <paramref name="email"/>.</summary>
    private async Task<HttpClient> SignInElsewhereAsync(string email = AuthTestExtensions.Email, string? userAgent = SafariOnIPhone)
    {
        var client = _factory.CreateClient();
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        (await client.LoginAsync(email)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return client;
    }

    private async Task<Guid> AddUserAsync(string email) => await UseDbAsync(async db =>
    {
        var user = new User { Email = email, DisplayName = "Someone", PasswordHash = new Argon2PasswordHasher().Hash(AuthTestExtensions.Password), CreatedAt = Start };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    });

    private static async Task<SessionList> SessionsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<SessionList>("/api/account/sessions"))!;

    // ---- the display name -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_changes_their_display_name_trimmed_and_is_answered_as_the_state_says()
    {
        var response = await _jos.PatchJsonAsync("/api/account", new { displayName = "  Jos N.  " });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answered = (await response.Content.ReadFromJsonAsync<CurrentUser>())!;
        answered.DisplayName.ShouldBe("Jos N.");
        (await _jos.GetStateAsync()).User.ShouldBe(answered);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task An_empty_display_name_is_refused(string? displayName)
    {
        var response = await _jos.PatchJsonAsync("/api/account", new { displayName });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.ReadErrorAsync();
        (error.Code, error.Field).ShouldBe(("display_name_invalid", "displayName"));
        (await _jos.GetStateAsync()).User!.DisplayName.ShouldBe("Jos");
    }

    [Fact]
    public async Task A_display_name_may_be_200_characters_and_no_more()
    {
        (await _jos.PatchJsonAsync("/api/account", new { displayName = new string('a', 200) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _jos.PatchJsonAsync("/api/account", new { displayName = new string('b', 201) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _jos.GetStateAsync()).User!.DisplayName.ShouldBe(new string('a', 200));
    }

    [Fact]
    public async Task A_display_name_changes_only_the_person_who_asks()
    {
        await AddUserAsync("other@example.com");
        var other = await SignInElsewhereAsync("other@example.com");

        (await other.PatchJsonAsync("/api/account", new { displayName = "Changed" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _jos.GetStateAsync()).User!.DisplayName.ShouldBe("Jos");
    }

    // ---- the list of devices ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_this_device_first_with_when_and_what_and_never_a_session_key()
    {
        _clock.Advance(TimeSpan.FromHours(1));
        var phone = await SignInElsewhereAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        (await phone.GetAsync("/api/auth/state")).EnsureSuccessStatusCode();

        var response = await _jos.GetAsync("/api/account/sessions");
        var body = await response.Content.ReadAsStringAsync();
        var list = (await response.Content.ReadFromJsonAsync<SessionList>())!;

        list.More.ShouldBe(0);
        list.Sessions.Select(s => (s.Current, s.UserAgent, s.SignedInAt)).ShouldBe(
        [
            (true, ChromeOnWindows, Start),
            (false, SafariOnIPhone, Start.AddHours(1)),
        ]);
        list.Sessions[1].LastSeenAt.ShouldBe(Start.AddHours(1).AddMinutes(5));
        // A handle, not the key the cookie holds: no key appears anywhere in the answer.
        var keys = await UseDbAsync(db => db.Sessions.Select(s => s.Id).ToListAsync());
        keys.Count.ShouldBe(2);
        foreach (var key in keys)
        {
            body.ShouldNotContain(key);
        }

        list.Sessions.Select(s => s.Id).ShouldBe(keys.Select(PostgresTicketStore.HandleOf), ignoreOrder: true);
    }

    [Fact]
    public async Task The_list_holds_neither_someone_elses_sessions_nor_expired_ones()
    {
        await AddUserAsync("other@example.com");
        await SignInElsewhereAsync("other@example.com");
        await UseDbAsync(async db =>
        {
            db.Sessions.Add(new Session
            {
                Id = "expired",
                UserId = (await db.Users.SingleAsync(u => u.Email == AuthTestExtensions.Email)).Id,
                Ticket = [1],
                CreatedAt = Start.AddDays(-40),
                LastSeenAt = Start.AddDays(-31),
                ExpiresAt = Start.AddDays(-1),
            });
            return await db.SaveChangesAsync();
        });

        (await SessionsAsync(_jos)).Sessions.ShouldHaveSingleItem().Current.ShouldBeTrue();
    }

    [Fact]
    public async Task The_list_shows_at_most_a_hundred_and_counts_the_rest()
    {
        await UseDbAsync(async db =>
        {
            var userId = (await db.Users.SingleAsync()).Id;
            for (var i = 0; i < 105; i++)
            {
                db.Sessions.Add(new Session
                {
                    Id = $"old-{i}",
                    UserId = userId,
                    Ticket = [1],
                    CreatedAt = Start.AddDays(-1),
                    LastSeenAt = Start.AddDays(-1).AddMinutes(i),
                    ExpiresAt = Start.AddDays(20),
                });
            }

            return await db.SaveChangesAsync();
        });

        var list = await SessionsAsync(_jos);

        list.Sessions.Count.ShouldBe(AccountEndpoints.MaxListed);
        list.More.ShouldBe(6);
        // This device first, whatever it was last seen; then the most recently seen.
        list.Sessions[0].Current.ShouldBeTrue();
        list.Sessions[1].Id.ShouldBe(PostgresTicketStore.HandleOf("old-104"));
    }

    // ---- signing a device out ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Signing_another_device_out_ends_its_session_on_its_next_request()
    {
        var phone = await SignInElsewhereAsync();
        var handle = (await SessionsAsync(_jos)).Sessions.Single(s => !s.Current).Id;

        (await _jos.DeleteJsonAsync($"/api/account/sessions/{handle}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await phone.GetStateAsync()).User.ShouldBeNull();
        (await phone.GetAsync("/api/workspaces")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
        (await SessionsAsync(_jos)).Sessions.ShouldHaveSingleItem().Current.ShouldBeTrue();
        (await UseDbAsync(db => db.AuditEvents.Select(e => e.Type).ToListAsync())).ShouldContain(AuditEventTypes.SessionRevoked);
    }

    [Fact]
    public async Task This_device_is_signed_out_through_the_menu_and_not_here()
    {
        var handle = (await SessionsAsync(_jos)).Sessions.Single().Id;

        var response = await _jos.DeleteJsonAsync($"/api/account/sessions/{handle}");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadErrorAsync()).Code.ShouldBe("session_is_current");
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Someone_elses_device_and_a_made_up_one_are_not_found_and_nothing_changes()
    {
        await AddUserAsync("other@example.com");
        var other = await SignInElsewhereAsync("other@example.com");
        var theirs = (await SessionsAsync(other)).Sessions.Single().Id;
        var key = await UseDbAsync(db => db.Sessions.Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s.Id, u.Email })
            .Where(x => x.Email == "other@example.com").Select(x => x.Id).SingleAsync());

        foreach (var handle in new[] { theirs, "not-a-session", key })
        {
            var response = await _jos.DeleteJsonAsync($"/api/account/sessions/{Uri.EscapeDataString(handle)}");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, handle);
            (await response.ReadErrorAsync()).Code.ShouldBe("session_not_found");
        }

        (await other.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Signing_out_all_other_devices_keeps_this_one_and_other_peoples()
    {
        var phone = await SignInElsewhereAsync();
        var tablet = await SignInElsewhereAsync(userAgent: null);
        await AddUserAsync("other@example.com");
        var other = await SignInElsewhereAsync("other@example.com");

        var response = await _jos.PostJsonAsync("/api/account/sessions/sign-out-others");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SessionsEnded>())!.Ended.ShouldBe(2);
        (await phone.GetStateAsync()).User.ShouldBeNull();
        (await tablet.GetStateAsync()).User.ShouldBeNull();
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
        (await other.GetStateAsync()).User.ShouldNotBeNull();
        (await UseDbAsync(db => db.AuditEvents.Select(e => e.Type).ToListAsync())).ShouldContain(AuditEventTypes.SessionsRevoked);
    }

    // ---- who may ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/account/sessions")]
    [InlineData("PATCH", "/api/account")]
    [InlineData("DELETE", "/api/account/sessions/x")]
    [InlineData("POST", "/api/account/sessions/sign-out-others")]
    public async Task Nobody_signed_out_reaches_any_of_it(string method, string path)
    {
        var anonymous = _factory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { displayName = "x" });
            request.Headers.Add(CsrfProtection.HeaderName, (await anonymous.GetStateAsync()).CsrfToken);
        }

        (await anonymous.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("PATCH", "/api/account")]
    [InlineData("DELETE", "/api/account/sessions/x")]
    [InlineData("POST", "/api/account/sessions/sign-out-others")]
    public async Task A_write_without_the_csrf_token_is_refused(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { displayName = "x" }) };

        (await _jos.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _jos.GetStateAsync()).User!.DisplayName.ShouldBe("Jos");
    }
}
