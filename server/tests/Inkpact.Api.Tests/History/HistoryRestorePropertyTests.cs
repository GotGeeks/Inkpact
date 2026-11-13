using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Auth;
using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.History;
using Inkpact.Api.Pages;
using Inkpact.Api.Permissions;
using Inkpact.Api.Sync;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.History;

/// <summary>
/// Generated schedules of edits, compactions, captures, renames, restores, restores refused because the page changed
/// meanwhile, and restores cut short at every stage (run B.5b, PR 4), against real Postgres, the real sidecar and the real
/// hub. After every step the page is checked against a replay of everything stored:
/// <list type="bullet">
/// <item>a restore leaves the page exactly the version it restored, and the page as it was exactly in its safety version;</item>
/// <item>a refused or cut-short restore changes no row at all;</item>
/// <item>every version reads back whole, and no safety version is lost while it is protected.</item>
/// </list>
/// </summary>
public class HistoryRestorePropertyTests(PostgresFixture postgres, ITestOutputHelper output) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const int PerStep = 3;
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] EmptyDocument = """{"type":"doc"}"""u8.ToArray();

    private static int Runs =>
        int.TryParse(Environment.GetEnvironmentVariable("INKPACT_PROPERTY_RUNS"), out var runs) && runs > 0 ? runs : 40;

    private readonly MutableTimeProvider _clock = new(Start);
    private readonly Dictionary<string, int> _outcomes = [];
    private InkpactFactory _factory = null!;
    private SyncUser _owner = null!;
    private Guid _workspaceId;
    private byte[] _firstParagraph = [];

    public async Task InitializeAsync()
    {
        _factory = new InkpactFactory(await postgres.CreateDatabaseAsync(), time: _clock, sidecar: true);
        _owner = await SyncUser.SetUpAdministratorAsync(_factory);
        await Service<YjsSidecar>().WaitUntilReadyAsync(new CancellationTokenSource(YjsSidecars.StartTimeout).Token);
        _firstParagraph = await Service<IYjsCodec>().BuildAsync(EmptyDocument, default);
        await using var scope = _factory.Services.CreateAsyncScope();
        _workspaceId = await scope.ServiceProvider.GetRequiredService<InkpactDbContext>().Workspaces.Select(w => w.Id).SingleAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void A_restore_gives_the_version_keeps_what_was_there_and_one_that_fails_changes_nothing()
    {
        var config = Config.QuickThrowOnFailure.WithMaxTest(Runs).WithEndSize(120).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(Schedules(), numbers => RunScheduleAsync(numbers).GetAwaiter().GetResult()));

        output.WriteLine(string.Join(Environment.NewLine, _outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key,-24} {o.Value}")));
        // Floors: every kind of step must really have happened, or the property proves nothing about it.
        _outcomes.GetValueOrDefault("restore").ShouldBeGreaterThan(Runs / 2);
        _outcomes.GetValueOrDefault("restore page_changed").ShouldBeGreaterThan(Runs / 8);
        _outcomes.GetValueOrDefault("crash").ShouldBeGreaterThan(Runs / 4);
        _outcomes.GetValueOrDefault("compact Compacted").ShouldBeGreaterThan(Runs / 4);
        _outcomes.GetValueOrDefault("capture Captured").ShouldBeGreaterThan(Runs / 2);
    }

    /// <summary>Schedules as numbers, three to a step: the kind, and two arguments. Shrinks by dropping whole steps.</summary>
    private static Arbitrary<int[]> Schedules()
    {
        var step = Gen.ArrayOf(Gen.Choose(0, 9999), PerStep);
        var schedule = Gen.Sized(size => Gen.Choose(2, Math.Clamp(size / 6 + 3, 3, 18))
            .SelectMany(count => Gen.ListOf(step, count))
            .Select(steps => steps.SelectMany(s => s).ToArray()));

        IEnumerable<int[]> Shrink(int[] numbers)
        {
            var steps = numbers.Length / PerStep;
            for (var chunk = 0; chunk < steps; chunk++)
            {
                yield return numbers.Take(chunk * PerStep).Concat(numbers.Skip(chunk * PerStep + PerStep)).ToArray();
            }
        }

        return Arb.From(schedule, Shrink);
    }

    private async Task<bool> RunScheduleAsync(int[] numbers)
    {
        var stream = EditStreams.Get(80, numbers.Length);
        var pageId = await CreatePageAsync();
        var next = 0;
        // A version to restore exists from the start: the page as it was made.
        Count($"capture {await Service<HistoryCapture>().CaptureAsync(pageId, default)}");

        for (var i = 0; i < numbers.Length / PerStep; i++)
        {
            var (kind, a, b) = (numbers[i * PerStep] % 8, numbers[i * PerStep + 1], numbers[i * PerStep + 2]);
            _clock.Advance(TimeSpan.FromSeconds(1 + a % 120));
            switch (kind)
            {
                case <= 1:
                    var count = Math.Min(1 + a % 6, stream.Count - next);
                    foreach (var update in stream.Skip(next).Take(count))
                    {
                        await Service<PageUpdateStore>().AppendAsync(pageId, _owner.UserId, update, default);
                    }

                    next += count;
                    break;
                case 2:
                    Count($"compact {await Service<Compactor>().CompactAsync(pageId, default)}");
                    break;
                case 3:
                    Count($"capture {await Service<HistoryCapture>().CaptureAsync(pageId, default)}");
                    break;
                case 4:
                    (await _owner.Http.PatchJsonAsync($"/api/pages/{pageId}", new { title = $"Title {a % 5}" })).IsSuccessStatusCode.ShouldBeTrue();
                    break;
                case 5:
                    await RestoreAndCheckAsync(pageId, a);
                    break;
                case 6:
                    await StaleRestoreAndCheckAsync(pageId, a, stream[(next + b) % stream.Count]);
                    break;
                default:
                    await CrashAndCheckAsync(pageId, a, Enum.GetValues<RestoreStage>()[b % Enum.GetValues<RestoreStage>().Length]);
                    break;
            }

            await CheckVersionsAsync(pageId);
        }

        return true;
    }

    /// <summary>A restore of one of the page's versions: the page becomes it, and the page as it was is its safety version.</summary>
    private async Task RestoreAndCheckAsync(Guid pageId, int pick)
    {
        var version = await PickAsync(pageId, pick);
        var contentBefore = await ContentAsync(pageId);
        var titleBefore = await UseDbAsync(db => db.Pages.Where(p => p.Id == pageId).Select(p => p.Title).SingleAsync());

        var result = await RestoreAsync(pageId, version.Id, await MarkAsync(pageId), new NoHistoryRestoreHooks());

        result.Answer.ShouldNotBeNull(result.Code);
        (await ContentAsync(pageId)).ShouldBe(await StateContentAsync(version.State));
        (await UseDbAsync(db => db.Pages.Where(p => p.Id == pageId).Select(p => p.Title).SingleAsync())).ShouldBe(version.Title);
        var safety = await UseDbAsync(db => db.PageVersions.AsNoTracking().SingleAsync(v => v.Id == result.Answer!.BeforeVersionId));
        (safety.Kind, safety.Title).ShouldBe((PageVersionKind.BeforeRestore, titleBefore));
        (await StateContentAsync(safety.State)).ShouldBe(contentBefore);
        Count("restore");
    }

    /// <summary>An edit stored while the restore was being made: it is refused, and the edit is all that changed.</summary>
    private async Task StaleRestoreAndCheckAsync(Guid pageId, int pick, byte[] edit)
    {
        var version = await PickAsync(pageId, pick);
        var mark = await MarkAsync(pageId);
        string? after = null;
        var meanwhile = new Hooks(async stage =>
        {
            if (stage == RestoreStage.Prepared)
            {
                await Service<PageUpdateStore>().AppendAsync(pageId, _owner.UserId, edit, default);
                after = await SummaryAsync();
            }
        });

        var result = await RestoreAsync(pageId, version.Id, mark, meanwhile);

        (result.Status, result.Code).ShouldBe((StatusCodes.Status409Conflict, "page_changed"));
        (await SummaryAsync()).ShouldBe(after);
        Count("restore page_changed");
    }

    /// <summary>A restore cut short at a stage: no row changed.</summary>
    private async Task CrashAndCheckAsync(Guid pageId, int pick, RestoreStage stage)
    {
        var version = await PickAsync(pageId, pick);
        var mark = await MarkAsync(pageId);
        var before = await SummaryAsync();

        await Should.ThrowAsync<SimulatedCrash>(() => RestoreAsync(
            pageId, version.Id, mark, new Hooks(at => at == stage ? throw new SimulatedCrash() : Task.CompletedTask)));

        (await SummaryAsync()).ShouldBe(before, $"after a crash at {stage}");
        Count("crash");
    }

    /// <summary>Every version reads back whole, and every safety version that is still protected is there.</summary>
    private async Task CheckVersionsAsync(Guid pageId)
    {
        var versions = await UseDbAsync(db => db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId).ToListAsync());
        foreach (var version in versions)
        {
            (await Service<IYjsCodec>().ReadAsync([version.State], default)).Skipped.ShouldBe(0);
        }

        var restores = await UseDbAsync(db => db.PageRestores.AsNoTracking().Where(r => r.PageId == pageId).ToListAsync());
        foreach (var restore in restores)
        {
            versions.ShouldContain(v => v.Id == restore.BeforeVersionId, "a safety version went while it was protected");
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------

    private sealed class SimulatedCrash : Exception;

    private sealed class Hooks(Func<RestoreStage, Task> at) : IHistoryRestoreHooks
    {
        public Task AtAsync(RestoreStage stage, CancellationToken cancellationToken) => at(stage);
    }

    private void Count(string outcome) => _outcomes[outcome] = _outcomes.GetValueOrDefault(outcome) + 1;

    private T Service<T>()
        where T : notnull => _factory.Services.GetRequiredService<T>();

    private async Task<T> UseDbAsync<T>(Func<InkpactDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<InkpactDbContext>());
    }

    private async Task<Guid> CreatePageAsync()
    {
        var created = await _owner.Http.PostJsonAsync($"/api/workspaces/{_workspaceId}/pages", new { title = "Title 0" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("page").GetProperty("id").GetGuid();
    }

    private async Task<PageVersion> PickAsync(Guid pageId, int pick)
    {
        var versions = await UseDbAsync(db => db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToListAsync());
        return versions[pick % versions.Count];
    }

    private async Task<(string Content, int Version)> MarkAsync(Guid pageId)
    {
        var list = await _owner.Http.GetFromJsonAsync<VersionList>($"/api/pages/{pageId}/versions");
        var version = await UseDbAsync(db => db.Pages.Where(p => p.Id == pageId).Select(p => p.Version).SingleAsync());
        return (list!.PageContent, version);
    }

    private async Task<RestoreResult> RestoreAsync(Guid pageId, Guid versionId, (string Content, int Version) mark, IHistoryRestoreHooks hooks)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var restore = new HistoryRestore(
            services.GetRequiredService<InkpactDbContext>(),
            services.GetRequiredService<IYjsCodec>(),
            services.GetRequiredService<HistorySnapshotBuilder>(),
            services.GetRequiredService<TreeService>(),
            services.GetRequiredService<IPermissionService>(),
            services.GetRequiredService<SyncHub>(),
            services.GetRequiredService<SyncOptions>(),
            _clock,
            hooks,
            NullLogger<HistoryRestore>.Instance);
        return await restore.RestoreAsync(pageId, versionId, _owner.UserId, new RestoreRequest(Guid.NewGuid(), mark.Content, mark.Version), default);
    }

    /// <summary>The page as the editor shows it: a replay of everything stored, from the first paragraph on.</summary>
    private async Task<string> ContentAsync(Guid pageId)
    {
        var stored = await Service<PageUpdateStore>().LoadAsync(pageId, default);
        return Encoding.UTF8.GetString((await Service<IYjsCodec>().ReadAsync([_firstParagraph, .. stored.Inputs], default)).Json);
    }

    private async Task<string> StateContentAsync(byte[] state) =>
        Encoding.UTF8.GetString((await Service<IYjsCodec>().ReadAsync([state], default)).Json);

    /// <summary>Every row a restore writes, to compare before and after.</summary>
    private Task<string> SummaryAsync() => UseDbAsync(async db =>
    {
        var lines = new List<string>();
        lines.AddRange(await db.PageVersions.AsNoTracking().OrderBy(v => v.Id).Select(v => $"version {v.Id} {v.Kind} {v.Title} {v.ProtectedUntil}").ToListAsync());
        lines.AddRange(await db.PageUpdates.AsNoTracking().OrderBy(u => u.Seq).Select(u => $"update {u.Seq}").ToListAsync());
        lines.AddRange(await db.PageSnapshots.AsNoTracking().OrderBy(s => s.PageId).Select(s => $"snapshot {s.PageId} {s.Watermark} {s.UpdateCount}").ToListAsync());
        lines.AddRange(await db.Pages.AsNoTracking().OrderBy(p => p.Id).Select(p => $"page {p.Id} {p.Title} {p.Version} {p.UpdatedAt.UtcTicks} {p.ContentText} {p.FileText}").ToListAsync());
        lines.AddRange(await db.PageLinks.AsNoTracking().OrderBy(l => l.SourcePageId).ThenBy(l => l.TargetPageId).Select(l => $"link {l.SourcePageId} {l.TargetPageId}").ToListAsync());
        lines.AddRange(await db.PageHistoryStates.AsNoTracking().OrderBy(s => s.PageId).Select(s => $"history {s.PageId} {s.CapturedSeq} {s.CapturedUpdates}").ToListAsync());
        lines.AddRange(await db.PageRestores.AsNoTracking().OrderBy(r => r.OperationId).Select(r => $"restore {r.OperationId}").ToListAsync());
        lines.AddRange(await db.PageTitleChanges.AsNoTracking().OrderBy(c => c.Id).Select(c => $"title {c.Id}").ToListAsync());
        lines.Add($"tree {await db.Workspaces.Select(w => w.TreeVersion).SingleAsync()}");
        return string.Join("\n", lines);
    });
}
