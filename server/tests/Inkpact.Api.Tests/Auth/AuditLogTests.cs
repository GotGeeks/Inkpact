using System.Net;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Auth;

[Collection(PostgresCollection.Name)]
public class AuditLogTests(PostgresFixture postgres) : IAsyncLifetime
{
    private InkpactFactory _factory = null!;

    // The proxy address is trusted by default, so the forwarded address is what ends up in the log.
    public async Task InitializeAsync() => _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), remoteIp: "10.1.2.3");

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<List<AuditEvent>> EventsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().AuditEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
    }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AuditTest/1.0");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");
        return client;
    }

    private async Task<Guid> UserIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Users.Select(u => u.Id).SingleAsync();
    }

    [Fact]
    public async Task Setup_is_logged_with_who_where_and_with_what()
    {
        var client = NewClient();
        (await client.SetupAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var entry = (await EventsAsync()).ShouldHaveSingleItem();

        entry.Type.ShouldBe(AuditEventTypes.SetupCompleted);
        entry.UserId.ShouldBe(await UserIdAsync());
        entry.Email.ShouldBe(AuthTestExtensions.Email);
        entry.Ip.ShouldBe("203.0.113.7");
        entry.UserAgent.ShouldBe("AuditTest/1.0");
        entry.OccurredAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task A_refused_setup_is_not_logged_as_completed()
    {
        await NewClient().SetupAsync(setupCode: "WRONGCODE");

        (await EventsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_successful_login_is_logged_with_the_user()
    {
        await NewClient().SetupAsync();

        await NewClient().LoginAsync();

        var login = (await EventsAsync()).Last();
        login.Type.ShouldBe(AuditEventTypes.LoginSucceeded);
        login.UserId.ShouldBe(await UserIdAsync());
        login.Ip.ShouldBe("203.0.113.7");
    }

    [Fact]
    public async Task A_failed_login_for_an_existing_account_is_logged_with_the_user_and_the_typed_address()
    {
        await NewClient().SetupAsync();

        await NewClient().LoginAsync(email: "JOS@Example.com", password: "wrong password");

        var failure = (await EventsAsync()).Last();
        failure.Type.ShouldBe(AuditEventTypes.LoginFailed);
        failure.UserId.ShouldBe(await UserIdAsync());
        failure.Email.ShouldBe("JOS@Example.com");
    }

    [Fact]
    public async Task A_failed_login_for_an_unknown_address_is_logged_without_a_user()
    {
        await NewClient().LoginAsync(email: "nobody@example.com", password: "wrong password");

        var failure = (await EventsAsync()).ShouldHaveSingleItem();
        failure.Type.ShouldBe(AuditEventTypes.LoginFailed);
        failure.UserId.ShouldBeNull();
        failure.Email.ShouldBe("nobody@example.com");
    }

    [Fact]
    public async Task The_password_is_never_written_down()
    {
        await NewClient().SetupAsync();
        await NewClient().LoginAsync(password: "my secret attempt");

        var everything = string.Join('\n', (await EventsAsync()).Select(e => $"{e.Type} {e.Email} {e.Ip} {e.UserAgent}"));
        everything.ShouldNotContain("my secret attempt");
        everything.ShouldNotContain(AuthTestExtensions.Password);
    }

    [Fact]
    public async Task A_very_long_typed_address_is_cut_to_what_the_table_holds()
    {
        await NewClient().LoginAsync(email: new string('a', 600) + "@example.com", password: "x");

        (await EventsAsync()).ShouldHaveSingleItem().Email!.Length.ShouldBe(320);
    }

    [Fact]
    public async Task Logout_and_logout_everywhere_are_logged()
    {
        var client = NewClient();
        await client.SetupAsync();

        await client.PostJsonAsync("/api/auth/logout");
        await client.LoginAsync();
        await client.PostJsonAsync("/api/auth/logout-everywhere");

        (await EventsAsync()).Select(e => e.Type).ShouldBe(
        [
            AuditEventTypes.SetupCompleted,
            AuditEventTypes.Logout,
            AuditEventTypes.LoginSucceeded,
            AuditEventTypes.LogoutEverywhere,
        ]);
        (await EventsAsync()).Skip(1).ShouldAllBe(e => e.UserId != null);
    }

    [Fact]
    public async Task Signing_out_without_a_session_is_not_logged()
    {
        await NewClient().PostJsonAsync("/api/auth/logout");

        (await EventsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_the_user_keeps_their_history_without_the_link()
    {
        await NewClient().SetupAsync();
        await NewClient().LoginAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InkpactDbContext>();
            await db.WorkspaceMembers.ExecuteDeleteAsync();
            await db.Workspaces.ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();
        }

        var events = await EventsAsync();

        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.UserId == null);
        events.Select(e => e.Email).ShouldContain(AuthTestExtensions.Email);
    }

    [Fact]
    public async Task Events_come_out_in_the_order_they_happened()
    {
        var client = NewClient();
        await client.SetupAsync();
        for (var i = 0; i < 3; i++)
        {
            await client.LoginAsync(password: "wrong password");
        }

        var events = await EventsAsync();

        events.Select(e => e.OccurredAt).ShouldBe(events.Select(e => e.OccurredAt).Order());
        events.Select(e => e.Id).ShouldBe(events.Select(e => e.Id).Order());
    }
}
