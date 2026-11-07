using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Links;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Inkpact.Api.Tests.Links;

/// <summary>
/// The job that puts the links of the pages that exist from before the link index into it (run B.1): only pages that are
/// compacted and have no live updates (the compactor indexes the others), only their snapshot read and only its links asked
/// for, one transaction and the compactor's lock per page, and nothing it meets can stop the host.
/// </summary>
public class LinkBackfillTests(PostgresFixture postgres, YjsSidecarFixture yjs) : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    private static readonly Guid A = LinkIndexTests.Targets[0];
    private static readonly Guid B = LinkIndexTests.Targets[1];

    private LinkBackfill CreateBackfill(
        IYjsCodec? codec = null, Action<LinkIndexOptions>? configure = null, bool sidecarEnabled = true, string? connectionString = null)
    {
        var options = new LinkIndexOptions { RetryDelay = TimeSpan.FromMilliseconds(20) };
        configure?.Invoke(options);
        return new LinkBackfill(
            connectionString is null ? Factory : new TestDbContextFactory(connectionString),
            Store,
            codec ?? Codec,
            options,
            new SidecarOptions { Enabled = sidecarEnabled },
            NullLogger<LinkBackfill>.Instance);
    }

    /// <summary>
    /// A page as one is from before the index: compacted (a snapshot and nothing live) with links in it, and not indexed. The
    /// snapshot is written straight, as a compaction before this run did, without the links.
    /// </summary>
    private async Task<Guid> LegacyPageAsync(params Guid[] targets)
    {
        var pageId = await AddPageAsync();
        var update = await Codec.BuildAsync(LinkIndexTests.Document(LinkIndexTests.PageAddresses(targets)), default);
        var merged = await Codec.CompactAsync([update], default);
        await using var db = CreateContext();
        db.PageSnapshots.Add(new PageSnapshot { PageId = pageId, State = merged.State, StateVector = merged.StateVector, Watermark = 1, Version = 1, UpdateCount = 1, UpdatedAt = Start });
        await db.SaveChangesAsync();
        return pageId;
    }

    private async Task<Guid[]> IndexedAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return [.. (await db.PageLinks.AsNoTracking().Where(l => l.SourcePageId == pageId).Select(l => l.TargetPageId).ToListAsync()).Order()];
    }

    private async Task<int> VersionOfAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.LinksIndexed).SingleAsync();
    }

    /// <summary>The real codec, but asking for the links of the page whose snapshot is <paramref name="snapshot"/> throws <paramref name="error"/>.</summary>
    private IYjsCodec FailingFor(byte[] snapshot, Exception error)
    {
        var codec = Substitute.For<IYjsCodec>();
        codec.LinksAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var inputs = call.Arg<IReadOnlyList<byte[]>>();
                return inputs.Count == 1 && inputs[0].AsSpan().SequenceEqual(snapshot)
                    ? throw error
                    : Codec.LinksAsync(inputs, default);
            });
        return codec;
    }

    private async Task<byte[]> SnapshotOfAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.PageSnapshots.AsNoTracking().Where(s => s.PageId == pageId).Select(s => s.State).SingleAsync();
    }

    // ---- what it indexes ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_compacted_page_from_before_the_index_gets_its_links_and_is_marked_without_anything_else_changing()
    {
        var pageId = await LegacyPageAsync(B, A);
        var before = (await ReadStoredAsync(pageId)).Summary;

        var pass = await CreateBackfill().RunPassAsync(default);

        pass.ShouldBe(new BackfillPass(1, 0, 0, false));
        (await IndexedAsync(pageId)).ShouldBe([A, B]);
        (await VersionOfAsync(pageId)).ShouldBe(LinkIndex.Version);
        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_to_do_and_a_page_without_content_is_marked_too()
    {
        var linked = await LegacyPageAsync(A);
        var empty = await AddPageAsync();

        var first = await CreateBackfill().RunPassAsync(default);
        var second = await CreateBackfill().RunPassAsync(default);

        first.ShouldBe(new BackfillPass(2, 0, 0, false));
        second.ShouldBe(new BackfillPass(0, 0, 0, false));
        (await VersionOfAsync(empty)).ShouldBe(LinkIndex.Version);
        (await IndexedAsync(empty)).ShouldBeEmpty();
        (await IndexedAsync(linked)).ShouldBe([A]);
    }

    [Fact]
    public async Task A_page_with_live_updates_is_left_to_the_compactor_which_indexes_it_when_it_compacts_it()
    {
        var live = await AddPageAsync();
        await AppendAsync(live, [await Codec.BuildAsync(LinkIndexTests.Document($"/p/{A}"), default)]);

        var pass = await CreateBackfill().RunPassAsync(default);

        pass.ShouldBe(new BackfillPass(0, 0, 0, false));
        (await VersionOfAsync(live)).ShouldBe(0);
        (await CreateCompactor().CompactAsync(live, default)).ShouldBe(CompactionResult.Compacted);
        (await IndexedAsync(live)).ShouldBe([A]);
    }

    [Fact]
    public async Task Pages_are_indexed_batch_after_batch_and_a_page_in_the_trash_is_indexed_too()
    {
        var pages = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            pages.Add(await LegacyPageAsync(A));
        }

        await using (var db = CreateContext())
        {
            await db.Pages.Where(p => p.Id == pages[0]).ExecuteUpdateAsync(p => p.SetProperty(x => x.DeletedAt, Start));
        }

        var pass = await CreateBackfill(configure: options => options.BatchSize = 2).RunPassAsync(default);

        pass.Indexed.ShouldBe(5);
        foreach (var page in pages)
        {
            (await IndexedAsync(page)).ShouldBe([A]);
        }
    }

    [Fact]
    public async Task A_page_that_the_compactor_holds_is_left_to_it_and_indexed_by_a_later_pass()
    {
        var held = await LegacyPageAsync(A);
        var other = await LegacyPageAsync(B);
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Hooks.OnStage = async stage =>
        {
            if (stage == CompactionStage.Locked)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };
        var compaction = CreateCompactor().CompactAsync(held, default);
        await reached.Task;

        var pass = await CreateBackfill().RunPassAsync(default);

        pass.ShouldBe(new BackfillPass(1, 1, 0, false));
        (await IndexedAsync(other)).ShouldBe([B]);
        (await VersionOfAsync(held)).ShouldBe(0);
        release.SetResult();
        // Nothing live: the compactor has nothing to do, and indexes nothing either.
        (await compaction).ShouldBe(CompactionResult.NothingToDo);
        (await CreateBackfill().RunPassAsync(default)).ShouldBe(new BackfillPass(1, 0, 0, false));
        (await IndexedAsync(held)).ShouldBe([A]);
    }

    // ---- what goes wrong -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sidecar_that_is_down_ends_the_pass_without_marking_anything_and_the_next_pass_goes_on()
    {
        var first = await LegacyPageAsync(A);
        var second = await LegacyPageAsync(B);
        var codec = Substitute.For<IYjsCodec>();
        codec.LinksAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Guid>>(_ => throw new YjsUnavailableException("the sidecar is down"));

        var pass = await CreateBackfill(codec).RunPassAsync(default);

        pass.SidecarUnavailable.ShouldBeTrue();
        (await VersionOfAsync(first)).ShouldBe(0);
        (await VersionOfAsync(second)).ShouldBe(0);

        (await CreateBackfill().RunPassAsync(default)).ShouldBe(new BackfillPass(2, 0, 0, false));
    }

    [Fact]
    public async Task A_page_that_finds_the_sidecar_down_again_and_again_is_left_so_that_it_cannot_keep_taking_it_down()
    {
        var bad = await LegacyPageAsync(A);
        var good = await LegacyPageAsync(B);
        var backfill = CreateBackfill(FailingFor(await SnapshotOfAsync(bad), new YjsUnavailableException("killed: out of memory")));

        var passes = new List<BackfillPass>();
        for (var i = 0; i < 4; i++)
        {
            passes.Add(await backfill.RunPassAsync(default));
        }

        // The pages go in the order of their ids: if the bad one is first, three passes end on it, then it is left.
        passes.Last().SidecarUnavailable.ShouldBeFalse();
        (await VersionOfAsync(good)).ShouldBe(LinkIndex.Version);
        (await VersionOfAsync(bad)).ShouldBe(0);
        passes.Count(p => p.SidecarUnavailable).ShouldBe(3);
    }

    [Fact]
    public async Task A_page_whose_snapshot_does_not_decode_or_that_meets_another_error_is_failed_and_the_others_are_indexed()
    {
        var rejected = await LegacyPageAsync(A);
        var broken = await LegacyPageAsync(A);
        var good = await LegacyPageAsync(B);
        var codec = Substitute.For<IYjsCodec>();
        var (rejectedState, brokenState) = (await SnapshotOfAsync(rejected), await SnapshotOfAsync(broken));
        codec.LinksAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var input = call.Arg<IReadOnlyList<byte[]>>()[0];
                return input.AsSpan().SequenceEqual(rejectedState) ? throw new YjsRejectedException("unknown update")
                    : input.AsSpan().SequenceEqual(brokenState) ? throw new InvalidOperationException("the page went away")
                    : Codec.LinksAsync(call.Arg<IReadOnlyList<byte[]>>(), default);
            });

        var pass = await CreateBackfill(codec).RunPassAsync(default);

        pass.ShouldBe(new BackfillPass(1, 0, 2, false));
        (await VersionOfAsync(good)).ShouldBe(LinkIndex.Version);
        (await VersionOfAsync(rejected)).ShouldBe(0);
        (await VersionOfAsync(broken)).ShouldBe(0);
    }

    // ---- the job at start-up -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_job_at_start_up_indexes_every_page_and_ends()
    {
        var pageId = await LegacyPageAsync(A);
        using var job = CreateBackfill();

        await job.StartAsync(default);
        await job.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        (await IndexedAsync(pageId)).ShouldBe([A]);
        await job.StopAsync(default);
    }

    [Fact]
    public async Task A_job_that_is_off_or_has_no_sidecar_does_nothing()
    {
        var pageId = await LegacyPageAsync(A);
        using var off = CreateBackfill(configure: options => options.BackfillEnabled = false);
        using var noSidecar = CreateBackfill(sidecarEnabled: false);

        await off.StartAsync(default);
        await noSidecar.StartAsync(default);
        await off.ExecuteTask!;
        await noSidecar.ExecuteTask!;

        (await VersionOfAsync(pageId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_database_that_cannot_be_reached_never_stops_the_host_and_the_job_gives_up_after_its_retries()
    {
        using var job = CreateBackfill(
            configure: options => (options.MaxRetries, options.RetryDelay) = (2, TimeSpan.FromMilliseconds(10)),
            connectionString: "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");

        await job.StartAsync(default);

        // Ends by itself, without an exception: a BackgroundService that throws stops the host.
        await Should.NotThrowAsync(() => job.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60)));
        job.ExecuteTask!.IsFaulted.ShouldBeFalse();
    }
}
