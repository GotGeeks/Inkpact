using System.Security.Claims;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Auth;

public class PostgresTicketStoreTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _time = new(Start);

    private PostgresTicketStore CreateStore() => new(new ContextFactory(ConnectionString), _time);

    private async Task<Guid> AddUserAsync(string email = "jos@example.com")
    {
        await using var db = CreateContext();
        var user = new User { Email = email, DisplayName = "Jos", CreatedAt = Start };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private AuthenticationTicket CreateTicket(Guid userId, TimeSpan? lifetime = null, string? userAgent = null)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties { ExpiresUtc = Start + (lifetime ?? TimeSpan.FromDays(30)) };
        if (userAgent is not null)
        {
            properties.Items[PostgresTicketStore.UserAgentItem] = userAgent;
        }

        return new AuthenticationTicket(new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private async Task<Session> ReadSessionAsync(string key)
    {
        await using var db = CreateContext();
        return await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == key);
    }

    [Fact]
    public async Task A_stored_ticket_comes_back_with_its_user()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();

        var key = await store.StoreAsync(CreateTicket(userId));
        var ticket = await store.RetrieveAsync(key);

        ticket.ShouldNotBeNull();
        ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier).ShouldBe(userId.ToString());
        ticket.AuthenticationScheme.ShouldBe(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task The_key_is_long_random_and_url_safe()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();

        var keys = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            keys.Add(await store.StoreAsync(CreateTicket(userId)));
        }

        keys.Distinct().Count().ShouldBe(5);
        foreach (var key in keys)
        {
            key.Length.ShouldBe(43);
            key.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
        }
    }

    [Fact]
    public async Task An_unknown_key_gives_nothing()
    {
        (await CreateStore().RetrieveAsync("does-not-exist")).ShouldBeNull();
    }

    [Fact]
    public async Task An_expired_session_gives_nothing()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();
        var key = await store.StoreAsync(CreateTicket(userId, TimeSpan.FromHours(1)));

        _time.Advance(TimeSpan.FromMinutes(59));
        (await store.RetrieveAsync(key)).ShouldNotBeNull();

        _time.Advance(TimeSpan.FromMinutes(2));
        (await store.RetrieveAsync(key)).ShouldBeNull();
    }

    [Fact]
    public async Task A_removed_session_gives_nothing()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();
        var key = await store.StoreAsync(CreateTicket(userId));

        await store.RemoveAsync(key);

        (await store.RetrieveAsync(key)).ShouldBeNull();
    }

    [Fact]
    public async Task Removing_a_missing_session_is_not_an_error()
    {
        await CreateStore().RemoveAsync("does-not-exist");
    }

    [Fact]
    public async Task Renewing_moves_the_expiry()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();
        var key = await store.StoreAsync(CreateTicket(userId, TimeSpan.FromHours(1)));

        _time.Advance(TimeSpan.FromMinutes(50));
        var renewed = CreateTicket(userId, TimeSpan.FromHours(1));
        renewed.Properties.ExpiresUtc = _time.GetUtcNow().AddHours(1);
        await store.RenewAsync(key, renewed);

        _time.Advance(TimeSpan.FromMinutes(30));
        (await store.RetrieveAsync(key)).ShouldNotBeNull();
        (await ReadSessionAsync(key)).ExpiresAt.ShouldBe(Start.AddMinutes(110));
    }

    [Fact]
    public async Task Reading_touches_the_session_at_most_once_a_minute()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();
        var key = await store.StoreAsync(CreateTicket(userId));

        _time.Advance(TimeSpan.FromSeconds(30));
        await store.RetrieveAsync(key);
        (await ReadSessionAsync(key)).LastSeenAt.ShouldBe(Start);

        _time.Advance(TimeSpan.FromSeconds(31));
        await store.RetrieveAsync(key);
        (await ReadSessionAsync(key)).LastSeenAt.ShouldBe(Start.AddSeconds(61));
    }

    [Fact]
    public async Task The_user_agent_is_kept_with_the_session()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();

        var key = await store.StoreAsync(CreateTicket(userId, userAgent: "Firefox/150"));

        (await ReadSessionAsync(key)).UserAgent.ShouldBe("Firefox/150");
    }

    [Fact]
    public async Task All_sessions_of_one_user_can_be_removed_and_nobody_elses()
    {
        var jos = await AddUserAsync("jos@example.com");
        var anna = await AddUserAsync("anna@example.com");
        var store = CreateStore();
        var josKeys = new[] { await store.StoreAsync(CreateTicket(jos)), await store.StoreAsync(CreateTicket(jos)) };
        var annaKey = await store.StoreAsync(CreateTicket(anna));

        (await store.RemoveAllForUserAsync(jos)).ShouldBe(2);

        foreach (var key in josKeys)
        {
            (await store.RetrieveAsync(key)).ShouldBeNull();
        }

        (await store.RetrieveAsync(annaKey)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cleanup_removes_only_expired_sessions()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();
        var shortLived = await store.StoreAsync(CreateTicket(userId, TimeSpan.FromHours(1)));
        var longLived = await store.StoreAsync(CreateTicket(userId, TimeSpan.FromDays(30)));

        _time.Advance(TimeSpan.FromHours(2));
        (await store.DeleteExpiredAsync()).ShouldBe(1);

        await using var db = CreateContext();
        (await db.Sessions.Select(s => s.Id).ToListAsync()).ShouldBe([longLived]);
        shortLived.ShouldNotBe(longLived);
    }

    [Fact]
    public async Task Deleting_the_user_deletes_their_sessions()
    {
        var userId = await AddUserAsync();
        var store = CreateStore();
        var key = await store.StoreAsync(CreateTicket(userId));

        await using (var db = CreateContext())
        {
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
        }

        (await store.RetrieveAsync(key)).ShouldBeNull();
    }

    private sealed class ContextFactory(string connectionString) : IDbContextFactory<InkpactDbContext>
    {
        public InkpactDbContext CreateDbContext() => CreateContext(connectionString);
    }
}
