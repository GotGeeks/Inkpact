using System.Security.Cryptography;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Auth;

/// <summary>What the command talks through: the terminal, or a test's stand-in for one.</summary>
/// <param name="Input">Standard input, for <c>--password-stdin</c>.</param>
/// <param name="Output">Where the result goes.</param>
/// <param name="Error">Where a refusal goes, one line.</param>
/// <param name="Interactive">Whether a person sits at a terminal, so a password can be asked without showing it.</param>
/// <param name="ReadSecret">Reads one line from the terminal without echoing it; null when the input ended.</param>
public sealed record CommandConsole(TextReader Input, TextWriter Output, TextWriter Error, bool Interactive, Func<string?> ReadSecret)
{
    public static CommandConsole System() =>
        new(Console.In, Console.Out, Console.Error, !Console.IsInputRedirected, ReadSecretFromTerminal);

    private static string? ReadSecretFromTerminal()
    {
        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>
/// <c>dotnet Inkpact.Api.dll reset-password &lt;email&gt;</c>: a new password for an account, set by whoever can run a
/// command in the container, who is the trusted administrator. For the one administrator who forgot theirs, while there is
/// no reset by email (run 2.10).
/// <para>
/// It starts no web server, no worker and no migration: it only opens the database. In one transaction it writes the new
/// hash, removes every session of the account (as "sign out everywhere"), clears the login lockout of its address and
/// writes a <c>password_reset</c> audit event without an address. The running app is another process, so it cannot be
/// told: its sync sockets of that account close at the hub's next session check (<c>Sync:SessionCheckInterval</c>).
/// </para>
/// <para>
/// The password is asked twice on the terminal without showing it, or read from standard input with
/// <c>--password-stdin</c>, or made here with <c>--generate</c>. Never from an argument: the process list and the shell's
/// history would keep it.
/// </para>
/// </summary>
public static class ResetPasswordCommand
{
    public const string Name = "reset-password";
    public const int GeneratedLength = 24;

    // Without the characters that are easily mistaken for each other when read from a screen.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == Name;

    /// <summary>Runs the command and returns the exit code: 0 when the password was changed, 1 on any refusal.</summary>
    public static async Task<int> RunAsync(
        string[] args, IConfiguration config, CommandConsole console, TimeProvider time, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunCoreAsync(args, config, console, time, cancellationToken);
        }
        catch (Refusal refusal)
        {
            console.Error.WriteLine($"inkpact: {refusal.Message}");
            return 1;
        }
    }

    private sealed class Refusal(string message) : Exception(message);

    private static async Task<int> RunCoreAsync(
        string[] args, IConfiguration config, CommandConsole console, TimeProvider time, CancellationToken cancellationToken)
    {
        var (typedEmail, fromStdin, generate) = Parse(args);
        var email = AuthEndpoints.NormalizeEmail(typedEmail) ?? throw new Refusal($"\"{typedEmail}\" is not an email address.");

        var connectionString = config.GetConnectionString(DataServiceCollectionExtensions.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new Refusal("no database is configured (ConnectionStrings:Inkpact). Run the command where the app runs, such as inside its container.");
        }

        await using var db = new InkpactDbContext(new DbContextOptionsBuilder<InkpactDbContext>().UseNpgsql(connectionString).Options);
        await RequireCurrentDatabaseAsync(db, cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == email, cancellationToken)
            ?? throw new Refusal($"there is no account with the address {email}.");

        var password = generate ? Generate() : fromStdin ? ReadFromInput(console) : Ask(console);
        if (PasswordPolicy.Validate(password, user.Email) is { } problem)
        {
            throw new Refusal(problem switch
            {
                "password_too_short" => $"the password is too short: at least {PasswordPolicy.MinLength} characters.",
                "password_too_long" => $"the password is too long: at most {PasswordPolicy.MaxLength} characters.",
                _ => "the password may not be the email address.",
            });
        }

        var hash = new Argon2PasswordHasher().Hash(password);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        user.PasswordHash = hash;
        db.AuditEvents.Add(new AuditEvent { OccurredAt = time.GetUtcNow(), Type = AuditEventTypes.PasswordReset, UserId = user.Id });
        await db.SaveChangesAsync(cancellationToken);
        var sessions = await db.Sessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        var key = LoginThrottling.KeyFor(user.Email);
        await db.LoginThrottles.Where(t => t.Key == key).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        console.Output.WriteLine($"The password of {user.Email} is changed. Signed out of {sessions} session(s); a page that is open stops syncing within a few minutes.");
        if (generate)
        {
            console.Output.WriteLine($"The new password, shown this once: {password}");
        }

        return 0;
    }

    private static (string Email, bool FromStdin, bool Generate) Parse(string[] args)
    {
        string? email = null;
        var fromStdin = false;
        var generate = false;
        foreach (var arg in args.Skip(1))
        {
            switch (arg)
            {
                case "--password-stdin":
                    fromStdin = true;
                    break;
                case "--generate":
                    generate = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new Refusal($"unknown option {arg}. Usage: {Name} <email> [--password-stdin | --generate]");
                    }

                    if (email is not null)
                    {
                        // A second word is most likely a password. It is not used, and not repeated here.
                        throw new Refusal("one argument only, the email address. The password is never taken from an argument: it is asked for, or read with --password-stdin.");
                    }

                    email = arg;
                    break;
            }
        }

        if (email is null)
        {
            throw new Refusal($"which account? Usage: {Name} <email> [--password-stdin | --generate]");
        }

        if (fromStdin && generate)
        {
            throw new Refusal("--password-stdin and --generate do not go together.");
        }

        return (email, fromStdin, generate);
    }

    /// <summary>The database must be exactly the version of this app: not older (start the app once first), not newer.</summary>
    private static async Task RequireCurrentDatabaseAsync(InkpactDbContext db, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> applied;
        try
        {
            applied = [.. await db.Database.GetAppliedMigrationsAsync(cancellationToken)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new Refusal($"the database cannot be reached ({exception.GetType().Name}).");
        }

        var known = db.Database.GetMigrations().ToList();
        if (applied.Except(known).Any())
        {
            throw new Refusal("the database was migrated by a newer version of Inkpact than this one. Use the version the app runs.");
        }

        if (known.Except(applied).Any())
        {
            throw new Refusal("the database has migrations waiting. Start the app once, so it migrates (and backs up) the database, then run this again.");
        }
    }

    private static string ReadFromInput(CommandConsole console) =>
        console.Input.ReadLine() ?? throw new Refusal("no password on standard input.");

    private static string Ask(CommandConsole console)
    {
        if (!console.Interactive)
        {
            throw new Refusal("no terminal to ask the password on. Run it with a terminal (docker exec -it), or use --password-stdin or --generate.");
        }

        console.Output.Write("New password: ");
        var first = console.ReadSecret() ?? throw new Refusal("no password was typed.");
        console.Output.Write("Once more: ");
        var second = console.ReadSecret() ?? throw new Refusal("no password was typed.");
        return first == second ? first : throw new Refusal("the two passwords are not the same. Nothing was changed.");
    }

    private static string Generate() => RandomNumberGenerator.GetString(Alphabet, GeneratedLength);
}
