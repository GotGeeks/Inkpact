using Inkpact.Api.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Compaction;

/// <summary>
/// An appender and a compactor running at the same time on one page (scenario 13 of the spike), with random crashes and
/// parallel attempts. However it goes, the page ends as the page an uncompacted replay gives, and every update is
/// either live or archived, once. The counts go to the test output. INKPACT_STRESS=1 runs three 50,000-edit streams
/// instead of one of 5,000.
/// </summary>
public class CompactionStressTests(PostgresFixture postgres, YjsSidecarFixture yjs, ITestOutputHelper output)
    : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    protected override bool PooledConnections => true;

    [Fact]
    public async Task An_appender_and_a_compactor_at_once_never_lose_or_change_anything()
    {
        var heavy = Environment.GetEnvironmentVariable("INKPACT_STRESS") == "1";
        foreach (var seed in heavy ? new[] { 31, 32, 33 } : [31])
        {
            await RunAsync(heavy ? 50_000 : 5_000, seed);
        }
    }

    private async Task RunAsync(int edits, int seed)
    {
        var stream = EditStreams.Get(edits, seed);
        var pageId = await AddPageAsync();
        Options.FullHistoryEvery = 5;
        var random = new Random(seed);
        var stages = Enum.GetValues<CompactionStage>();
        var seqs = new List<long>();
        var counts = new Dictionary<string, int>();
        void Count(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

        var appender = Task.Run(async () =>
        {
            foreach (var update in stream)
            {
                seqs.Add(await Store.AppendAsync(pageId, UserId, update, default));
            }
        });

        var compactor = CreateCompactor();
        while (!appender.IsCompleted)
        {
            Options.MaxUpdatesPerCompaction = random.Next(50, 2_000);
            Hooks.OnStage = null;
            var roll = random.Next(10);
            if (roll < 6)
            {
                Count($"compact {await compactor.CompactAsync(pageId, default)}");
            }
            else if (roll < 8)
            {
                var crashAt = stages[random.Next(stages.Length)];
                Hooks.OnStage = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;
                try
                {
                    Count($"crash-attempt {await compactor.CompactAsync(pageId, default)}");
                }
                catch (SimulatedCrash)
                {
                    Count("crash");
                }
            }
            else
            {
                foreach (var result in await Task.WhenAll(compactor.CompactAsync(pageId, default), compactor.CompactAsync(pageId, default)))
                {
                    Count($"parallel {result}");
                }
            }

            await Task.Delay(5);
        }

        await appender;
        Hooks.OnStage = null;
        Options.MaxUpdatesPerCompaction = 5_000;
        while (await compactor.CompactAsync(pageId, default) == CompactionResult.Compacted)
        {
            Count("final compact");
        }

        var stored = await ReadStoredAsync(pageId);
        var oracle = await Codec.CompactAsync(stream, default);
        var page = await Codec.CompactAsync((await Store.LoadAsync(pageId, default)).Inputs, default);

        output.WriteLine($"{edits} edits (seed {seed}), {stream.Count} updates, snapshot {stored.Snapshot!.State.Length} bytes, version {stored.Snapshot.Version}");
        output.WriteLine(string.Join(Environment.NewLine, counts.OrderBy(c => c.Key).Select(c => $"  {c.Key,-28} {c.Value}")));

        stored.LiveSeqs.ShouldBeEmpty();
        stored.ArchivedSeqs.Order().ShouldBe(seqs.Order());
        stored.Snapshot.UpdateCount.ShouldBe(stream.Count);
        page.ReplayFingerprint.ShouldBe(oracle.ReplayFingerprint);
        page.Text.ShouldBe(oracle.Text);
        stored.Text.ShouldBe(oracle.Text.Replace("\0", ""));
        counts.GetValueOrDefault("compact Compacted").ShouldBeGreaterThan(5, "the compactor must have really run while appends were coming in");
        counts.GetValueOrDefault("crash").ShouldBeGreaterThan(0);
    }
}
