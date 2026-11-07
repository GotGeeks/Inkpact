using Inkpact.Api.Compaction;
using Inkpact.Api.Crdt;
using Inkpact.Api.Links;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Inkpact.Api.Tests.Links;

/// <summary>
/// The job that puts the links of the pages that exist from before the link index into it (run B.1): read-only on the page
/// content, one transaction and the compactor's lock per page, and never in the way of a compaction.
/// </summary>
public class LinkBackfillTests(PostgresFixture postgres, YjsSidecarFixture yjs) : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    private static readonly Guid A = LinkIndexTests.Targets[0];
    private static readonly Guid B = LinkIndexTests.Targets[1];

    private LinkBackfill CreateBackfill(IYjsCodec? codec = null, Action<LinkIndexOptions>? configure = null)
    {
        var options = new LinkIndexOptions { RetryDelay = TimeSpan.FromMilliseconds(20) };
        configure?.Invoke(options);
        return new LinkBackfill(Factory, Store, codec ?? Codec, options, NullLogger<LinkBackfill>.Instance);
    }

    /// <summary>A page as it is from before the index: content with links, nothing indexed.</summary>
    private async Task<Guid> PageWithLinksAsync(params Guid[] targets)
    {
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, [await Codec.BuildAsync(LinkIndexTests.Document(LinkIndexTests.PageAddresses(targets)), default)]);
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

    [Fact]
    public async Task A_page_from_before_the_index_gets_its_links_and_is_marked_without_being_compacted()
    {
        var pageId = await PageWithLinksAsync(B, A);
        var before = (await ReadStoredAsync(pageId)).Summary;

        var pass = await CreateBackfill().RunPassAsync(default);

        pass.ShouldBe(new BackfillPass(1, 0, 0, false));
        (await IndexedAsync(pageId)).ShouldBe([A, B]);
        (await VersionOfAsync(pageId)).ShouldBe(LinkIndex.Version);
        // Read-only: no snapshot, no archive, the live updates as they were.
        (await ReadStoredAsync(pageId)).Summary.ShouldBe(before);
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_to_do_and_a_page_without_content_is_marked_too()
    {
        var linked = await PageWithLinksAsync(A);
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
    public async Task Pages_are_indexed_batch_after_batch_and_a_page_in_the_trash_is_indexed_too()
    {
        var pages = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            pages.Add(await PageWithLinksAsync(A));
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
    public async Task A_page_that_is_being_compacted_is_left_to_the_compactor_which_indexes_it_itself()
    {
        var pageId = await PageWithLinksAsync(A);
        var other = await PageWithLinksAsync(B);
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        Hooks.OnStage = async stage =>
        {
            if (stage == CompactionStage.Read)
            {
                reached.TrySetResult();
                await release.Task;
            }
        };
        var compaction = CreateCompactor().CompactAsync(pageId, default);
        await reached.Task;

        var pass = await CreateBackfill().RunPassAsync(default);

        pass.ShouldBe(new BackfillPass(1, 1, 0, false));
        (await IndexedAsync(other)).ShouldBe([B]);
        (await IndexedAsync(pageId)).ShouldBeEmpty();
        release.SetResult();
        (await compaction).ShouldBe(CompactionResult.Compacted);
        (await IndexedAsync(pageId)).ShouldBe([A]);
        (await CreateBackfill().RunPassAsync(default)).ShouldBe(new BackfillPass(0, 0, 0, false));
    }

    [Fact]
    public async Task A_sidecar_that_is_down_ends_the_pass_without_marking_anything_and_the_next_pass_goes_on()
    {
        var first = await PageWithLinksAsync(A);
        var second = await PageWithLinksAsync(B);
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns<YjsCompaction>(_ => throw new YjsUnavailableException("the sidecar is down"));

        var pass = await CreateBackfill(codec).RunPassAsync(default);

        pass.SidecarUnavailable.ShouldBeTrue();
        (await VersionOfAsync(first)).ShouldBe(0);
        (await VersionOfAsync(second)).ShouldBe(0);

        (await CreateBackfill().RunPassAsync(default)).ShouldBe(new BackfillPass(2, 0, 0, false));
        (await IndexedAsync(first)).ShouldBe([A]);
        (await IndexedAsync(second)).ShouldBe([B]);
    }

    [Fact]
    public async Task A_page_whose_updates_do_not_decode_is_counted_as_failed_and_the_others_are_still_indexed()
    {
        var bad = await PageWithLinksAsync(A);
        var good = await PageWithLinksAsync(B);
        var calls = 0;
        var codec = Substitute.For<IYjsCodec>();
        codec.CompactAsync(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(call => ++calls == 1
                ? throw new YjsRejectedException("unknown update")
                : Codec.CompactAsync(call.Arg<IReadOnlyList<byte[]>>(), default));

        var pass = await CreateBackfill(codec).RunPassAsync(default);

        pass.Indexed.ShouldBe(1);
        pass.Failed.ShouldBe(1);
        // Which page went first is the order of their ids: the one that failed is not marked, the other is.
        (await VersionOfAsync(bad) + await VersionOfAsync(good)).ShouldBe(LinkIndex.Version);
    }

    [Fact]
    public async Task The_job_at_start_up_indexes_every_page_and_ends_and_one_that_is_off_does_nothing()
    {
        var pageId = await PageWithLinksAsync(A);
        using var off = CreateBackfill(configure: options => options.BackfillEnabled = false);
        await off.StartAsync(default);
        await off.ExecuteTask!;
        (await VersionOfAsync(pageId)).ShouldBe(0);

        using var on = CreateBackfill();
        await on.StartAsync(default);
        await on.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        (await IndexedAsync(pageId)).ShouldBe([A]);
        await on.StopAsync(default);
    }
}
