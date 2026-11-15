using System.Net;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Inkpact.Api.Tests.Account;

/// <summary>
/// A person changes their own password from the settings screen (run B.9, PR 2): the current one asked and guarded as a
/// login is, the new one under the setup rules, and every other device signed out in the same transaction. With real
/// sessions and the real lockout in Postgres; the sockets that close with them are in SyncHubTests.
/// </summary>
public class PasswordChangeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string NewPassword = "a whole new passphrase";

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private InkpactFactory _factory = null!;
    private HttpClient _jos = null!;

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: _clock);
        _jos = await _factory.SignedInClientAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string? currentPassword, string? newPassword) =>
        client.PostJsonAsync("/api/account/password", new { currentPassword, newPassword });

    private async Task<HttpClient> SignInElsewhereAsync(string password = AuthTestExtensions.Password)
    {
        var client = _factory.CreateClient();
        (await client.LoginAsync(password: password)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return client;
    }

    private Task<List<string>> AuditTypesAsync() => UseDbAsync(db => db.AuditEvents.OrderBy(e => e.Id).Select(e => e.Type).ToListAsync());

    private Task<string?> HashAsync() => UseDbAsync(db => db.Users.Select(u => u.PasswordHash).SingleAsync());

    // ---- a change -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_right_current_password_changes_it_and_signs_out_every_other_device_but_this_one()
    {
        var phone = await SignInElsewhereAsync();
        var userId = (await _jos.GetStateAsync()).User!.Id;
        await UseDbAsync(async db =>
        {
            // An expired session goes too: none may outlive the password it was signed in with.
            db.Sessions.Add(new Session
            {
                Id = "expired",
                UserId = userId,
                Ticket = [1],
                CreatedAt = _clock.GetUtcNow().AddDays(-40),
                LastSeenAt = _clock.GetUtcNow().AddDays(-31),
                ExpiresAt = _clock.GetUtcNow().AddDays(-1),
            });
            return await db.SaveChangesAsync();
        });
        // One wrong attempt, so there is a count to clear.
        (await ChangeAsync(_jos, "not the password", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var response = await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
        (await phone.GetStateAsync()).User.ShouldBeNull();
        (await phone.GetAsync("/api/workspaces")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await UseDbAsync(db => db.Sessions.CountAsync())).ShouldBe(1);
        (await UseDbAsync(db => db.LoginThrottles.CountAsync())).ShouldBe(0);
        (await AuditTypesAsync()).ShouldContain(AuditEventTypes.PasswordChanged);
        var changed = await UseDbAsync(db => db.AuditEvents.SingleAsync(e => e.Type == AuditEventTypes.PasswordChanged));
        changed.UserId.ShouldBe(userId);
        changed.Email.ShouldBeNull();

        using var laptop = _factory.CreateClient();
        (await laptop.LoginAsync(password: AuthTestExtensions.Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await laptop.LoginAsync(password: NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task This_device_can_change_it_again_with_the_new_one()
    {
        (await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ChangeAsync(_jos, NewPassword, "and yet another passphrase")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---- a wrong current password, and the lockout ---------------------------------------------------------------------

    [Fact]
    public async Task A_wrong_current_password_is_counted_written_down_and_changes_nothing_else()
    {
        var phone = await SignInElsewhereAsync();
        var hashBefore = await HashAsync();

        var response = await ChangeAsync(_jos, "not the password", NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.ReadErrorAsync();
        (error.Code, error.Field).ShouldBe(("current_password_invalid", "currentPassword"));
        (await HashAsync()).ShouldBe(hashBefore);
        (await phone.GetStateAsync()).User.ShouldNotBeNull();
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
        (await UseDbAsync(db => db.LoginThrottles.SingleAsync())).Failures.ShouldBe(1);
        var failed = await UseDbAsync(db => db.AuditEvents.SingleAsync(e => e.Type == AuditEventTypes.PasswordChangeFailed));
        failed.UserId.ShouldBe((await _jos.GetStateAsync()).User!.Id);
        (await AuditTypesAsync()).ShouldNotContain(AuditEventTypes.PasswordChanged);
    }

    [Fact]
    public async Task Five_wrong_current_passwords_lock_the_email_for_the_change_and_for_login()
    {
        for (var i = 0; i < 5; i++)
        {
            (await ChangeAsync(_jos, "not the password", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // The right password is refused too while it is locked, with the wait.
        var locked = await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword);
        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await locked.ReadErrorAsync()).Code.ShouldBe("login_locked");
        locked.Headers.RetryAfter!.ToString().ShouldBe("60");
        (await AuditTypesAsync()).ShouldContain(AuditEventTypes.LoginLocked);

        using (var phone = _factory.CreateClient())
        {
            (await phone.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        }

        // This device stays signed in: the lock is on guessing, not on sessions that exist.
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();

        _clock.Advance(TimeSpan.FromSeconds(61));
        (await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Failed_logins_count_toward_the_same_lock()
    {
        using (var stranger = _factory.CreateClient())
        {
            for (var i = 0; i < 5; i++)
            {
                (await stranger.LoginAsync(password: "a guess at it")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }
        }

        (await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task While_locked_the_hash_is_still_worked_out_so_the_answer_takes_as_long()
    {
        var hasher = new CountingHasher();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(hasher)));
        var client = factory.CreateClient();
        (await client.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        for (var i = 0; i < 5; i++)
        {
            (await ChangeAsync(client, "not the password", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var before = hasher.Verifications;
        (await ChangeAsync(client, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        hasher.Verifications.ShouldBe(before + 1);
        hasher.DummyVerifications.ShouldBe(1);
    }

    [Fact]
    public async Task The_change_shares_the_per_address_limit_with_login()
    {
        await using var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), perAddressPerMinute: 3, time: _clock);
        var client = await factory.SignedInClientAsync();
        (await ChangeAsync(client, "not the password", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var over = await ChangeAsync(client, AuthTestExtensions.Password, NewPassword);

        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await over.ReadErrorAsync()).Code.ShouldBe("too_many_requests");
    }

    // ---- the new password's rules ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("short", "password_too_short")]
    [InlineData(null, "password_too_short")]
    [InlineData(AuthTestExtensions.Email, "password_same_as_email")]
    [InlineData(" JOS@example.com ", "password_same_as_email")]
    public async Task A_new_password_against_the_rules_is_refused_before_the_current_one_is_checked(string? newPassword, string code)
    {
        var hashBefore = await HashAsync();

        var response = await ChangeAsync(_jos, "not even the right one", newPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.ReadErrorAsync();
        (error.Code, error.Field).ShouldBe((code, "newPassword"));
        (await HashAsync()).ShouldBe(hashBefore);
        // Nothing was checked, so nothing counts toward the lock.
        (await UseDbAsync(db => db.LoginThrottles.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task A_new_password_may_be_128_characters_and_no_more()
    {
        var tooLong = await ChangeAsync(_jos, AuthTestExtensions.Password, new string('x', 129));
        (await tooLong.ReadErrorAsync()).Code.ShouldBe("password_too_long");

        (await ChangeAsync(_jos, AuthTestExtensions.Password, new string('x', 128))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_same_password_again_is_refused_and_signs_nobody_out()
    {
        var phone = await SignInElsewhereAsync();

        var response = await ChangeAsync(_jos, AuthTestExtensions.Password, AuthTestExtensions.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.ReadErrorAsync();
        (error.Code, error.Field).ShouldBe(("password_unchanged", "newPassword"));
        (await phone.GetStateAsync()).User.ShouldNotBeNull();
        (await AuditTypesAsync()).ShouldNotContain(AuditEventTypes.PasswordChanged);
    }

    // ---- all or nothing ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failure_in_the_transaction_changes_nothing()
    {
        var phone = await SignInElsewhereAsync();
        (await ChangeAsync(_jos, "not the password", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var hashBefore = await HashAsync();
        // The event is the transaction's last write: refusing it must undo the hash, the sessions and the lockout's reset.
        await UseDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            create function test_refuse_password_changed() returns trigger as $$
            begin
              if new.type = 'password_changed' then
                raise exception 'refused by the test';
              end if;
              return new;
            end;
            $$ language plpgsql;

            create trigger test_refuse_password_changed_trigger before insert on audit_events
              for each row execute function test_refuse_password_changed();
            """));

        var response = await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await HashAsync()).ShouldBe(hashBefore);
        (await phone.GetStateAsync()).User.ShouldNotBeNull();
        (await UseDbAsync(db => db.Sessions.CountAsync())).ShouldBe(2);
        (await UseDbAsync(db => db.LoginThrottles.SingleAsync())).Failures.ShouldBe(1);
        using var laptop = _factory.CreateClient();
        (await laptop.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_password_changed_meanwhile_is_not_overwritten()
    {
        // Another change commits between this request's check and its write: the write finds a different hash.
        var hasher = new CountingHasher();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(hasher)));
        var client = factory.CreateClient();
        (await client.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var phone = await SignInElsewhereAsync();
        string? changedMeanwhile = null;
        hasher.BeforeHash = async () =>
        {
            hasher.BeforeHash = null;
            (await ChangeAsync(phone, AuthTestExtensions.Password, "the phone's passphrase")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            changedMeanwhile = await HashAsync();
        };

        var response = await ChangeAsync(client, AuthTestExtensions.Password, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadErrorAsync()).Code.ShouldBe("current_password_invalid");
        (await HashAsync()).ShouldBe(changedMeanwhile);
        (await phone.GetStateAsync()).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_device_signed_out_meanwhile_changes_nothing()
    {
        var hasher = new CountingHasher();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(hasher)));
        var laptop = factory.CreateClient();
        (await laptop.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var hashBefore = await HashAsync();
        // While the laptop's new hash is worked out, the phone signs every other device out, the laptop among them.
        hasher.BeforeHash = async () =>
        {
            hasher.BeforeHash = null;
            (await _jos.PostJsonAsync("/api/account/sessions/sign-out-others")).StatusCode.ShouldBe(HttpStatusCode.OK);
        };

        var response = await ChangeAsync(laptop, AuthTestExtensions.Password, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadErrorAsync()).Code.ShouldBe("not_authenticated");
        (await HashAsync()).ShouldBe(hashBefore);
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
    }

    // ---- a login at the same moment ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_login_that_checked_the_old_password_before_a_change_keeps_no_session()
    {
        var hasher = new CountingHasher();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(hasher)));
        // The old password is checked; before the login stores its session, the password is changed on this device.
        hasher.AfterVerify = async () =>
        {
            hasher.AfterVerify = null;
            (await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        };
        var stranger = factory.CreateClient();

        var response = await stranger.LoginAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadErrorAsync()).Code.ShouldBe("invalid_credentials");
        (await stranger.GetStateAsync()).User.ShouldBeNull();
        (await UseDbAsync(db => db.Sessions.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task A_login_that_upgrades_the_old_hash_does_not_undo_a_change()
    {
        var hasher = new CountingHasher { RehashAll = true };
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(hasher)));
        // The login checked the old password and upgrades its hash; meanwhile the password is changed on this device.
        hasher.BeforeHash = async () =>
        {
            hasher.BeforeHash = null;
            (await ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        };
        var changed = factory.CreateClient();

        (await changed.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var laptop = _factory.CreateClient();
        (await laptop.LoginAsync(password: AuthTestExtensions.Password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await laptop.LoginAsync(password: NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_change_waits_for_a_login_that_holds_the_account_and_then_removes_its_session()
    {
        // A login that checked the old password holds the user's row while it stores its session. Its insert is held up
        // here (a test trigger waits on an advisory lock), so the change starts while the login holds the row.
        const long key = 0x50_41_53_53;
        var connectionString = await UseDbAsync(db => Task.FromResult(db.Database.GetConnectionString()))
            ?? throw new InvalidOperationException("No connection string.");
        await UseDbAsync(db => db.Database.ExecuteSqlRawAsync($"""
            create function test_hold_session_insert() returns trigger as $$
            begin
              perform pg_advisory_xact_lock({key});
              return new;
            end;
            $$ language plpgsql;

            create trigger test_hold_session_insert_trigger before insert on sessions
              for each row execute function test_hold_session_insert();
            """));
        // Not pooled: closing it ends the session, and with it the lock, whatever happens to the test.
        await using var lockConnection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await lockConnection.OpenAsync();
        await using (var take = new NpgsqlCommand($"select pg_advisory_lock({key})", lockConnection))
        {
            await take.ExecuteNonQueryAsync();
        }

        var stranger = _factory.CreateClient();
        Task<HttpResponseMessage> login;
        Task<HttpResponseMessage> change;
        try
        {
            login = stranger.LoginAsync();
            // Only waiters in this database count: the server is shared with the other classes.
            await WaitForAsync(lockConnection, "l.locktype = 'advisory'", login);
            change = ChangeAsync(_jos, AuthTestExtensions.Password, NewPassword);
            await WaitForAsync(lockConnection, "l.locktype <> 'advisory'", change);
        }
        finally
        {
            await using var release = new NpgsqlCommand($"select pg_advisory_unlock({key})", lockConnection);
            await release.ExecuteNonQueryAsync();
        }

        (await login).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await change).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        // The login's session was stored before the change removed the sessions: it is gone.
        (await stranger.GetStateAsync()).User.ShouldBeNull();
        (await UseDbAsync(db => db.Sessions.CountAsync())).ShouldBe(1);
        (await _jos.GetStateAsync()).User.ShouldNotBeNull();
    }

    /// <summary>Waits until a request of this database waits on a lock of the kind given; fails if the request ends first.</summary>
    private static async Task WaitForAsync(NpgsqlConnection connection, string kind, Task request)
    {
        await using var waiting = new NpgsqlCommand(
            $"""
            select exists (select 1 from pg_locks l join pg_stat_activity a on a.pid = l.pid
              where not l.granted and a.datname = current_database() and {kind})
            """,
            connection);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(bool)(await waiting.ExecuteScalarAsync())!)
        {
            if (request.IsCompleted)
            {
                await request;
                throw new InvalidOperationException($"The request ended without waiting on a lock ({kind}).");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Nothing waited on a lock ({kind}) within 30 seconds.");
            }

            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_hash_only_upgraded_by_a_login_meanwhile_still_takes_the_change()
    {
        var changing = new CountingHasher();
        await using var changeFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(changing)));
        await using var upgradeFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IPasswordHasher>(new CountingHasher { RehashAll = true })));
        var laptop = changeFactory.CreateClient();
        (await laptop.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var hashBefore = await HashAsync();
        // The current password is checked; before the change is written, a login elsewhere upgrades the same password's hash.
        changing.BeforeHash = async () =>
        {
            changing.BeforeHash = null;
            (await upgradeFactory.CreateClient().LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await HashAsync()).ShouldNotBe(hashBefore);
        };

        (await ChangeAsync(laptop, AuthTestExtensions.Password, NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var phone = _factory.CreateClient();
        (await phone.LoginAsync(password: NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_login_whose_commit_fails_leaves_no_session()
    {
        // Refused at the commit, after the session was stored on its own connection.
        await UseDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            create function test_refuse_login() returns trigger as $$
            begin
              if new.type = 'login_succeeded' then
                raise exception 'refused by the test';
              end if;
              return new;
            end;
            $$ language plpgsql;

            create constraint trigger test_refuse_login_trigger after insert on audit_events
              deferrable initially deferred for each row execute function test_refuse_login();
            """));
        var stranger = _factory.CreateClient();

        (await stranger.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        (await stranger.GetStateAsync()).User.ShouldBeNull();
        (await UseDbAsync(db => db.Sessions.CountAsync())).ShouldBe(1);
        (await UseDbAsync(db => db.AuditEvents.CountAsync(e => e.Type == AuditEventTypes.LoginSucceeded))).ShouldBe(0);
    }

    /// <summary>The real hasher, counting its verifications; and pauses after a check and before a hash, for a change to slip in.</summary>
    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly Argon2PasswordHasher _inner = new();
        private int _verifications;
        private int _dummyVerifications;

        public int Verifications => _verifications;

        public int DummyVerifications => _dummyVerifications;

        public Func<Task>? BeforeHash { get; set; }

        public Func<Task>? AfterVerify { get; set; }

        /// <summary>Every hash is old, as after the parameters changed: a login upgrades it.</summary>
        public bool RehashAll { get; set; }

        public string Hash(string password)
        {
            BeforeHash?.Invoke().GetAwaiter().GetResult();
            return _inner.Hash(password);
        }

        public bool Verify(string hash, string password)
        {
            Interlocked.Increment(ref _verifications);
            var right = _inner.Verify(hash, password);
            AfterVerify?.Invoke().GetAwaiter().GetResult();
            return right;
        }

        public bool NeedsRehash(string hash) => RehashAll || _inner.NeedsRehash(hash);

        public void VerifyDummy(string password)
        {
            Interlocked.Increment(ref _verifications);
            Interlocked.Increment(ref _dummyVerifications);
            _inner.VerifyDummy(password);
        }
    }
}
