using Inkpact.Api.Auth;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.Auth;

public class AuditRetentionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private async Task<InkpactFactory> StartAsync(MutableTimeProvider clock, params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string> { ["Audit:PurgeEnabled"] = "false" };
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        var factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: clock, settings: all);
        factory.CreateClient().Dispose();
        return factory;
    }

    private static async Task<T> UseDbAsync<T>(InkpactFactory factory, Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private static Task AddEventsAsync(InkpactFactory factory, params (string Type, DateTimeOffset At)[] events) =>
        UseDbAsync(factory, async db =>
        {
            db.AuditEvents.AddRange(events.Select(e => new AuditEvent { Type = e.Type, OccurredAt = e.At }));
            return await db.SaveChangesAsync();
        });

    private static Task<List<string>> TypesAsync(InkpactFactory factory) =>
        UseDbAsync(factory, db => db.AuditEvents.OrderBy(a => a.Id).Select(a => a.Type).ToListAsync());

    [Fact]
    public async Task Events_older_than_the_retention_go_and_the_one_on_the_boundary_stays()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now));
        var boundary = Now - TimeSpan.FromDays(365);
        await AddEventsAsync(
            factory,
            ("just_before", boundary - TimeSpan.FromSeconds(1)),
            ("at", boundary),
            ("just_after", boundary + TimeSpan.FromSeconds(1)),
            ("today", Now));

        var purged = await factory.Services.GetRequiredService<AuditRetention>().PurgeAsync(CancellationToken.None);

        purged.ShouldBe(1);
        (await TypesAsync(factory)).ShouldBe(["at", "just_after", "today"]);
    }

    [Fact]
    public async Task Every_type_follows_the_same_rule()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now));
        var old = Now - TimeSpan.FromDays(400);
        await AddEventsAsync(
            factory,
            (AuditEventTypes.LoginFailed, old),
            (AuditEventTypes.LoginSucceeded, old),
            (AuditEventTypes.WorkspaceExported, old),
            (AuditEventTypes.SetupCompleted, old));

        await factory.Services.GetRequiredService<AuditRetention>().PurgeAsync(CancellationToken.None);

        (await TypesAsync(factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Many_events_go_in_batches_and_all_of_them_go()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now), ("Audit:PurgeBatchSize", "3"));
        var old = Now - TimeSpan.FromDays(500);
        await AddEventsAsync(factory, [.. Enumerable.Range(0, 10).Select(i => ("old", old + TimeSpan.FromMinutes(i))), ("kept", Now)]);

        var purged = await factory.Services.GetRequiredService<AuditRetention>().PurgeAsync(CancellationToken.None);

        purged.ShouldBe(10);
        (await TypesAsync(factory)).ShouldBe(["kept"]);
    }

    [Fact]
    public async Task A_run_that_is_cut_short_has_deleted_whole_batches_and_the_next_run_finishes()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now), ("Audit:PurgeBatchSize", "2"));
        var old = Now - TimeSpan.FromDays(500);
        await AddEventsAsync(factory, [.. Enumerable.Range(0, 6).Select(i => ($"old{i}", old + TimeSpan.FromMinutes(i)))]);
        var retention = factory.Services.GetRequiredService<AuditRetention>();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => retention.PurgeAsync(cancelled.Token));
        (await TypesAsync(factory)).Count.ShouldBe(6);

        (await retention.PurgeAsync(CancellationToken.None)).ShouldBe(6);
        (await TypesAsync(factory)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Zero_keeps_everything()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now), ("Audit:RetentionDays", "0"));
        await AddEventsAsync(factory, ("ancient", Now - TimeSpan.FromDays(5000)));

        (await factory.Services.GetRequiredService<AuditRetention>().PurgeAsync(CancellationToken.None)).ShouldBe(0);

        (await TypesAsync(factory)).ShouldBe(["ancient"]);
    }

    [Fact]
    public async Task A_shorter_retention_is_taken_from_the_configuration()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now), ("Audit:RetentionDays", "30"));
        await AddEventsAsync(factory, ("old", Now - TimeSpan.FromDays(31)), ("recent", Now - TimeSpan.FromDays(29)));

        await factory.Services.GetRequiredService<AuditRetention>().PurgeAsync(CancellationToken.None);

        (await TypesAsync(factory)).ShouldBe(["recent"]);
    }

    [Fact]
    public async Task Nothing_else_is_deleted()
    {
        await using var factory = await StartAsync(new MutableTimeProvider(Now));
        using var client = factory.CreateClient();
        await client.SetupAsync();
        await AddEventsAsync(factory, ("old", Now - TimeSpan.FromDays(400)));

        await factory.Services.GetRequiredService<AuditRetention>().PurgeAsync(CancellationToken.None);

        (await UseDbAsync(factory, db => db.Users.CountAsync())).ShouldBe(1);
        (await UseDbAsync(factory, db => db.Workspaces.CountAsync())).ShouldBe(1);
        (await TypesAsync(factory)).ShouldBe([AuditEventTypes.SetupCompleted]);
    }

    [Theory]
    [InlineData("RetentionDays", "-1")]
    [InlineData("PurgeBatchSize", "0")]
    [InlineData("PurgeInterval", "00:00:00")]
    public void A_setting_that_makes_no_sense_is_refused_by_name(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Audit:{key}"] = value }).Build();

        Should.Throw<InvalidOperationException>(() => AuditOptions.From(config)).Message.ShouldContain($"Audit:{key}");
    }
}
