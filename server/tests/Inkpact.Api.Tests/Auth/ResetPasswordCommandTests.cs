using System.Net;
using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Auth;

public class ResetPasswordCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string NewPassword = "a new and long password";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private string _connectionString = null!;
    private InkpactFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        _factory = new InkpactFactory(_connectionString);
        using var client = _factory.CreateClient();
        (await client.SetupAsync()).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record Run(int ExitCode, string Output, string Error);

    /// <summary>Runs the command as the console would: its arguments, what is on standard input, and what is typed at the prompts.</summary>
    private async Task<Run> RunAsync(string[] args, string stdin = "", string[]? typed = null, string? connectionString = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Inkpact"] = connectionString ?? _connectionString })
            .Build();
        var output = new StringWriter();
        var error = new StringWriter();
        var answers = new Queue<string>(typed ?? []);
        var console = new CommandConsole(
            new StringReader(stdin), output, error, Interactive: typed is not null, () => answers.TryDequeue(out var answer) ? answer : null);

        var exitCode = await ResetPasswordCommand.RunAsync(args, config, console, new MutableTimeProvider(Now));
        return new Run(exitCode, output.ToString(), error.ToString());
    }

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private Task<string?> HashAsync() => UseDbAsync(db => db.Users.Select(u => u.PasswordHash).SingleAsync());

    private async Task<HttpStatusCode> LoginAsync(string password)
    {
        using var client = _factory.CreateClient();
        return (await client.LoginAsync(password: password)).StatusCode;
    }

    [Fact]
    public async Task The_new_password_signs_in_and_the_old_one_no_longer_does()
    {
        var run = await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword]);

        run.ExitCode.ShouldBe(0, run.Error);
        run.Output.ShouldContain(AuthTestExtensions.Email);
        run.Output.ShouldNotContain(NewPassword);
        new Argon2PasswordHasher().Verify((await HashAsync())!, NewPassword).ShouldBeTrue();
        (await LoginAsync(NewPassword)).ShouldBe(HttpStatusCode.NoContent);
        (await LoginAsync(AuthTestExtensions.Password)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_address_may_be_typed_in_any_case()
    {
        var run = await RunAsync(["reset-password", AuthTestExtensions.Email.ToUpperInvariant()], typed: [NewPassword, NewPassword]);

        run.ExitCode.ShouldBe(0, run.Error);
        (await LoginAsync(NewPassword)).ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Every_session_of_the_account_ends_and_nobody_elses()
    {
        using var mine = _factory.CreateClient();
        (await mine.LoginAsync()).EnsureSuccessStatusCode();
        using var alsoMine = _factory.CreateClient();
        (await alsoMine.LoginAsync()).EnsureSuccessStatusCode();
        var other = await UseDbAsync(async db =>
        {
            var user = new User { Email = "other@example.com", DisplayName = "Other", PasswordHash = "x", CreatedAt = Now };
            db.Users.Add(user);
            db.Sessions.Add(new Session { Id = "others-session", UserId = user.Id, Ticket = [1], CreatedAt = Now, LastSeenAt = Now, ExpiresAt = Now.AddDays(1) });
            await db.SaveChangesAsync();
            return user.Id;
        });

        var run = await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword]);

        run.ExitCode.ShouldBe(0, run.Error);
        (await UseDbAsync(db => db.Sessions.Select(s => s.UserId).ToListAsync())).ShouldBe([other]);
        (await mine.GetStateAsync()).User.ShouldBeNull();
        (await alsoMine.GetStateAsync()).User.ShouldBeNull();
    }

    [Fact]
    public async Task A_lockout_of_the_address_is_cleared()
    {
        await using var factory = new InkpactFactory(_connectionString, failuresBeforeLock: 2);
        for (var i = 0; i < 2; i++)
        {
            using var client = factory.CreateClient();
            (await client.LoginAsync(password: "wrong password")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var locked = factory.CreateClient())
        {
            (await locked.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        }

        var run = await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword]);

        run.ExitCode.ShouldBe(0, run.Error);
        (await UseDbAsync(db => db.LoginThrottles.CountAsync())).ShouldBe(0);
        using var after = factory.CreateClient();
        (await after.LoginAsync(password: NewPassword)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_audit_event_is_written_for_the_account_without_an_address()
    {
        await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword]);

        var userId = await UseDbAsync(db => db.Users.Select(u => u.Id).SingleAsync());
        var written = await UseDbAsync(db => db.AuditEvents.SingleAsync(e => e.Type == AuditEventTypes.PasswordReset));
        written.UserId.ShouldBe(userId);
        written.OccurredAt.ShouldBe(Now);
        written.Ip.ShouldBeNull();
        written.UserAgent.ShouldBeNull();
        written.Email.ShouldBeNull();
    }

    [Fact]
    public async Task The_password_can_come_from_standard_input()
    {
        var run = await RunAsync(["reset-password", AuthTestExtensions.Email, "--password-stdin"], stdin: NewPassword + "\n");

        run.ExitCode.ShouldBe(0, run.Error);
        run.Output.ShouldNotContain(NewPassword);
        (await LoginAsync(NewPassword)).ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_generated_password_is_24_characters_shown_once_and_signs_in()
    {
        var run = await RunAsync(["reset-password", AuthTestExtensions.Email, "--generate"]);

        run.ExitCode.ShouldBe(0, run.Error);
        var generated = run.Output.TrimEnd().Split(' ')[^1];
        generated.Length.ShouldBe(ResetPasswordCommand.GeneratedLength);
        (await LoginAsync(generated)).ShouldBe(HttpStatusCode.NoContent);

        var again = await RunAsync(["reset-password", AuthTestExtensions.Email, "--generate"]);
        again.Output.TrimEnd().Split(' ')[^1].ShouldNotBe(generated);
    }

    public static TheoryData<string[], string[]?, string, string> Refusals => new()
    {
        { ["reset-password"], null, "", "which account" },
        { ["reset-password", "nobody@example.com"], [NewPassword, NewPassword], "", "no account" },
        { ["reset-password", "not an address"], [NewPassword, NewPassword], "", "not an email address" },
        { ["reset-password", AuthTestExtensions.Email], ["short", "short"], "", "too short" },
        { ["reset-password", AuthTestExtensions.Email], [new string('x', 129), new string('x', 129)], "", "too long" },
        { ["reset-password", AuthTestExtensions.Email], [AuthTestExtensions.Email, AuthTestExtensions.Email], "", "may not be the email address" },
        { ["reset-password", AuthTestExtensions.Email], [NewPassword, NewPassword + "x"], "", "not the same" },
        { ["reset-password", AuthTestExtensions.Email, NewPassword], null, "", "never taken from an argument" },
        { ["reset-password", AuthTestExtensions.Email, "--password", NewPassword], null, "", "unknown option" },
        { ["reset-password", AuthTestExtensions.Email, "--password-stdin", "--generate"], null, "", "do not go together" },
        { ["reset-password", AuthTestExtensions.Email, "--password-stdin"], null, "", "no password on standard input" },
        { ["reset-password", AuthTestExtensions.Email], null, NewPassword + "\n", "no terminal" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refusal_is_one_line_a_non_zero_exit_code_and_changes_nothing(string[] args, string[]? typed, string stdin, string expected)
    {
        var before = await HashAsync();

        var run = await RunAsync(args, stdin, typed);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain(expected);
        run.Error.TrimEnd().ShouldNotContain("\n");
        run.Error.ShouldNotContain(NewPassword);
        run.Output.ShouldNotContain(NewPassword);
        (await HashAsync()).ShouldBe(before);
        (await UseDbAsync(db => db.AuditEvents.CountAsync(e => e.Type == AuditEventTypes.PasswordReset))).ShouldBe(0);
    }

    [Fact]
    public async Task A_database_with_a_migration_waiting_is_refused()
    {
        await UseDbAsync(async db =>
        {
            var newest = (await db.Database.GetAppliedMigrationsAsync()).Last();
            return await db.Database.ExecuteSqlAsync($"delete from \"__EFMigrationsHistory\" where \"MigrationId\" = {newest}");
        });
        var before = await HashAsync();

        var run = await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword]);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("migrations waiting");
        (await HashAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_database_from_a_newer_version_is_refused()
    {
        await UseDbAsync(db => db.Database.ExecuteSqlAsync(
            $"insert into \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") values ('99991231235959_FromTheFuture', '99.0.0')"));
        var before = await HashAsync();

        var run = await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword]);

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("newer version");
        (await HashAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_is_refused_without_its_address_or_password()
    {
        var run = await RunAsync(
            ["reset-password", AuthTestExtensions.Email],
            typed: [NewPassword, NewPassword],
            connectionString: "Host=127.0.0.1;Port=1;Database=nothing;Username=u;Password=hunter2secret;Timeout=2");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("cannot be reached");
        run.Error.ShouldNotContain("hunter2secret");
    }

    [Fact]
    public async Task Without_a_connection_string_it_says_so()
    {
        var run = await RunAsync(["reset-password", AuthTestExtensions.Email], typed: [NewPassword, NewPassword], connectionString: " ");

        run.ExitCode.ShouldBe(1);
        run.Error.ShouldContain("ConnectionStrings:Inkpact");
    }

    [Fact]
    public void Only_the_first_argument_makes_it_the_command()
    {
        ResetPasswordCommand.IsCommand(["reset-password", "a@example.com"]).ShouldBeTrue();
        ResetPasswordCommand.IsCommand([]).ShouldBeFalse();
        ResetPasswordCommand.IsCommand(["--urls", "reset-password"]).ShouldBeFalse();
    }
}
