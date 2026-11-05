using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Inkpact.Api.Tests.Compaction;

/// <summary>
/// Random schedules of appends, compactions, crashes and concurrent compactions over a stream of real Yjs updates
/// (scenario 12 of the spike as a model test). After every step the page as stored (snapshot plus live updates) must be
/// the page an uncompacted replay of everything appended gives, in fingerprint and in text, and every update must be in
/// exactly one of the live log and the archive. The oracle never touches the database.
///
/// A failing schedule is shrunk to a short one and FsCheck prints the seed, so it can be replayed. Set
/// INKPACT_PROPERTY_RUNS for a longer run (the default is 100 schedules).
/// </summary>
public class CompactionPropertyTests(PostgresFixture postgres, YjsSidecarFixture yjs, ITestOutputHelper output)
    : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    // How often each kind of step ended each way, over all schedules. A generator that never produced a crash, or a
    // compaction that skipped, would make this test pass without proving anything.
    private readonly Dictionary<string, int> _outcomes = [];

    private static int Runs =>
        int.TryParse(Environment.GetEnvironmentVariable("INKPACT_PROPERTY_RUNS"), out var runs) && runs > 0 ? runs : 100;

    private const int PerStep = 4;

    [Fact]
    public void The_page_is_always_what_an_uncompacted_replay_gives()
    {
        var config = Config.QuickThrowOnFailure.WithMaxTest(Runs).WithEndSize(200).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(Schedules(), numbers => RunScheduleAsync(numbers).GetAwaiter().GetResult()));

        output.WriteLine(string.Join(Environment.NewLine, _outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key,-30} {o.Value}")));

        // Floors, not exact numbers: every kind of step must really have happened.
        _outcomes.GetValueOrDefault("append").ShouldBeGreaterThan(100);
        _outcomes.GetValueOrDefault("compact Compacted").ShouldBeGreaterThan(50);
        _outcomes.GetValueOrDefault("compact NothingToDo").ShouldBeGreaterThan(5);
        _outcomes.GetValueOrDefault("crash").ShouldBeGreaterThan(20);
        _outcomes.GetValueOrDefault("twice Compacted+Skipped").ShouldBeGreaterThan(5);
        // The interleaving of issue #64, held on purpose: the second compaction runs after the first has committed and
        // folds the next batch. Under a batch limit below the backlog both compact.
        _outcomes.GetValueOrDefault("twice held Compacted+Compacted").ShouldBeGreaterThan(2);
        _outcomes.GetValueOrDefault("load").ShouldBeGreaterThan(20);
    }

    [Fact]
    public async Task Compacting_at_every_point_of_a_stream_gives_the_page_the_whole_stream_gives()
    {
        // The stream has updates delivered out of causal order, so some cuts fall between an update and the one it needs:
        // the snapshot then holds a pending struct, which the fingerprint does not show. The text after the rest has
        // arrived does.
        var stream = EditStreams.Get(120, 11);
        var oracle = await Codec.CompactAsync(stream, default);

        for (var cut = 1; cut < stream.Count; cut++)
        {
            var pageId = await AddPageAsync();
            await AppendBulkAsync(pageId, stream);
            Options.MaxUpdatesPerCompaction = cut;

            var compactor = CreateCompactor();
            (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted, $"cut {cut}");
            Options.MaxUpdatesPerCompaction = 5_000;
            (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted, $"cut {cut}");
            (await compactor.CompactAsync(pageId, default)).ShouldBe(CompactionResult.NothingToDo, $"cut {cut}");

            var page = await Codec.CompactAsync((await Store.LoadAsync(pageId, default)).Inputs, default);
            page.ReplayFingerprint.ShouldBe(oracle.ReplayFingerprint, $"cut {cut}");
            page.Text.ShouldBe(oracle.Text, $"cut {cut}");
        }
    }

    /// <summary>
    /// Schedules as numbers, four to a step. The kind of step comes from the first, the rest are its arguments. Shrinks
    /// by dropping whole steps.
    /// </summary>
    private static Arbitrary<int[]> Schedules()
    {
        var step = Gen.ArrayOf(Gen.Choose(0, 9999), PerStep);
        var schedule = Gen.Sized(size => Gen.Choose(1, Math.Clamp(size / 8 + 2, 2, 24))
            .SelectMany(count => Gen.ListOf(step, count))
            .Select(steps => steps.SelectMany(s => s).ToArray()));

        IEnumerable<int[]> Shrink(int[] numbers)
        {
            var steps = numbers.Length / PerStep;
            if (steps > 3)
            {
                yield return numbers.Take(steps / 2 * PerStep).ToArray();
                yield return numbers.Skip(steps / 2 * PerStep).ToArray();
            }

            for (var chunk = 0; chunk < steps; chunk++)
            {
                yield return numbers.Take(chunk * PerStep).Concat(numbers.Skip(chunk * PerStep + PerStep)).ToArray();
            }
        }

        return Arb.From(schedule, Shrink);
    }

    private async Task<bool> RunScheduleAsync(int[] numbers)
    {
        var stream = EditStreams.Get(150, 21);
        var pageId = await AddPageAsync();
        var appended = new List<byte[]>();
        var seqs = new List<long>();
        var stages = Enum.GetValues<CompactionStage>();
        var compactor = CreateCompactor();
        Options.FullHistoryEvery = 2;

        for (var i = 0; i < numbers.Length / PerStep; i++)
        {
            var (kind, a, b) = (numbers[i * PerStep] % 8, numbers[i * PerStep + 1], numbers[i * PerStep + 2]);
            Options.MaxUpdatesPerCompaction = b % 3 == 0 ? 3 + b % 40 : 5_000;
            Hooks.OnStage = null;

            switch (kind)
            {
                case <= 2:
                    {
                        var count = Math.Min(1 + a % 40, stream.Count - appended.Count);
                        foreach (var update in stream.Skip(appended.Count).Take(count))
                        {
                            seqs.Add(await Store.AppendAsync(pageId, UserId, update, default));
                            appended.Add(update);
                        }

                        Count("append", count > 0 ? 1 : 0);
                        break;
                    }

                case 3 or 4:
                    Count($"compact {await compactor.CompactAsync(pageId, default)}");
                    break;

                case 5:
                    {
                        var before = (await ReadStoredAsync(pageId)).Summary;
                        var crashAt = stages[a % stages.Length];
                        Hooks.OnStage = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;
                        try
                        {
                            // Nothing to do or skipped stages are not crashes: the schedule may have nothing to compact.
                            var result = await compactor.CompactAsync(pageId, default);
                            result.ShouldBe(CompactionResult.NothingToDo);
                            Count("crash", 0);
                        }
                        catch (SimulatedCrash)
                        {
                            (await ReadStoredAsync(pageId)).Summary.ShouldBe(before, $"after a crash at {crashAt}");
                            Count("crash");
                        }

                        break;
                    }

                case 6:
                    {
                        // Two compactions of the page started together. The lock lets one in at a time; it does not make
                        // them overlap. When the first has committed before the second asks for the lock, the second runs
                        // and folds the next batch of what is still live (issue #64). Half the time the two race as they
                        // will; the other half the second is held after it has begun and before it asks for the lock until
                        // the first has committed, so that this interleaving is tested on purpose and not left to timing.
                        var forced = a % 2 == 1;
                        if (forced)
                        {
                            // Held, the second only has work under a batch limit below the backlog: always set one here.
                            Options.MaxUpdatesPerCompaction = 3 + b % 20;
                        }

                        var limit = Options.MaxUpdatesPerCompaction;
                        var before = await ReadStoredAsync(pageId);
                        CompactionResult[] results;
                        if (forced)
                        {
                            var held = new TaskCompletionSource();
                            var release = new TaskCompletionSource();
                            var secondHooks = new TestHooks
                            {
                                OnStage = async stage =>
                                {
                                    if (stage == CompactionStage.Begun)
                                    {
                                        held.TrySetResult();
                                        await release.Task;
                                    }
                                },
                            };
                            var second = CreateCompactor(hooks: secondHooks).CompactAsync(pageId, default);
                            await held.Task;
                            var first = await compactor.CompactAsync(pageId, default);
                            var between = await ReadStoredAsync(pageId);
                            release.SetResult();
                            results = [first, await second];

                            // Each one on its own: a batch of at most the limit, of updates the other did not take.
                            CheckOneCompaction(before, between, first, limit);
                            CheckOneCompaction(between, await ReadStoredAsync(pageId), results[1], limit);
                        }
                        else
                        {
                            results = await Task.WhenAll(compactor.CompactAsync(pageId, default), compactor.CompactAsync(pageId, default));
                        }

                        // Both together, raced or held. Which updates each took cannot be told apart in a free race, so this
                        // checks the totals: one version and one batch per compaction, and every merged update counted once.
                        var after = await ReadStoredAsync(pageId);
                        results.ShouldAllBe(r => r == CompactionResult.Compacted || r == CompactionResult.Skipped || r == CompactionResult.NothingToDo);
                        var compacted = results.Count(r => r == CompactionResult.Compacted);
                        var merged = before.LiveSeqs.Length - after.LiveSeqs.Length;
                        (after.Snapshot?.Version ?? 0).ShouldBe((before.Snapshot?.Version ?? 0) + compacted);
                        ((after.Snapshot?.UpdateCount ?? 0) - (before.Snapshot?.UpdateCount ?? 0)).ShouldBe(merged);
                        merged.ShouldBe(Math.Min(before.LiveSeqs.Length, compacted * limit));
                        if (compacted == 2)
                        {
                            // Only a batch limit leaves anything for a second compaction.
                            before.LiveSeqs.Length.ShouldBeGreaterThan(limit);
                        }

                        Count($"twice {(forced ? "held " : "")}{string.Join("+", results.Order())}");
                        break;
                    }

                default:
                    {
                        // A load in the middle of the schedule: the page as a subscriber would get it.
                        await CheckPageAsync(pageId, appended, seqs);
                        Count("load");
                        break;
                    }
            }

            await CheckPageAsync(pageId, appended, seqs);
        }

        return true;
    }

    /// <summary>
    /// One compaction between two readings of the page: compacted, it took at most the limit, raised the version by one
    /// and counted what it took once; otherwise it changed nothing.
    /// </summary>
    private static void CheckOneCompaction(StoredPage from, StoredPage to, CompactionResult result, int limit)
    {
        var merged = from.LiveSeqs.Length - to.LiveSeqs.Length;
        var versions = (to.Snapshot?.Version ?? 0) - (from.Snapshot?.Version ?? 0);
        var counted = (to.Snapshot?.UpdateCount ?? 0) - (from.Snapshot?.UpdateCount ?? 0);
        if (result == CompactionResult.Compacted)
        {
            merged.ShouldBe(Math.Min(from.LiveSeqs.Length, limit));
            merged.ShouldBeGreaterThan(0);
            versions.ShouldBe(1);
            counted.ShouldBe(merged);
        }
        else
        {
            result.ShouldBe(CompactionResult.NothingToDo);
            merged.ShouldBe(0);
            versions.ShouldBe(0);
        }
    }

    private void Count(string outcome, int by = 1) => _outcomes[outcome] = _outcomes.GetValueOrDefault(outcome) + by;

    /// <summary>The page as stored equals the oracle, and every appended update is in exactly one place.</summary>
    private async Task CheckPageAsync(Guid pageId, List<byte[]> appended, List<long> seqs)
    {
        var stored = await ReadStoredAsync(pageId);
        stored.LiveSeqs.Intersect(stored.ArchivedSeqs).ShouldBeEmpty();
        stored.LiveSeqs.Concat(stored.ArchivedSeqs).Order().ShouldBe(seqs.Order());

        var page = await Codec.CompactAsync((await Store.LoadAsync(pageId, default)).Inputs, default);
        var oracle = await Codec.CompactAsync(appended, default);
        page.ReplayFingerprint.ShouldBe(oracle.ReplayFingerprint);
        page.Text.ShouldBe(oracle.Text);

        // Once nothing is live, the stored text is the text of the page.
        if (stored.LiveSeqs.Length == 0 && stored.Snapshot is not null)
        {
            stored.Text.ShouldBe(oracle.Text.Replace("\0", ""));
            stored.Snapshot.StateVector.ShouldBe(oracle.StateVector);
        }
    }
}
