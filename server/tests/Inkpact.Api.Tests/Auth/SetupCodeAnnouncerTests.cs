using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inkpact.Api.Tests.Auth;

public class SetupCodeAnnouncerTests(PostgresFixture postgres) : DatabaseTestBase(postgres)
{
    private async Task<List<string>> RunAnnouncerAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<InkpactDbContext>(_ => CreateContext());
        await using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<SetupCodeAnnouncer>();

        await new SetupCodeAnnouncer(provider, SetupCode.Create("ABCD2345"), logger).StartAsync(default);

        return logger.Messages;
    }

    [Fact]
    public async Task The_code_is_written_to_the_log_while_there_is_no_account()
    {
        var messages = await RunAnnouncerAsync();

        messages.ShouldHaveSingleItem().ShouldContain("ABCD2345");
    }

    [Fact]
    public async Task The_code_stays_out_of_the_log_once_an_account_exists()
    {
        await using (var db = CreateContext())
        {
            db.Users.Add(new User { Email = "jos@example.com", DisplayName = "Jos", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        (await RunAnnouncerAsync()).ShouldBeEmpty();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
