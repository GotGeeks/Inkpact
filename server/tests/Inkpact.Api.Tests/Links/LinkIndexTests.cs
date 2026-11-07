using System.Text;
using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Compaction;
using Inkpact.Api.Links;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Links;

/// <summary>
/// The link index (run B.1): the compactor puts the links of what it merged into <c>page_links</c> in its own transaction, so
/// the index is never ahead of or behind the snapshot, whatever happens to a compaction. The real Postgres and the real Yjs
/// sidecar; the pages are made by the sidecar from ProseMirror JSON, with link marks.
/// </summary>
public class LinkIndexTests(PostgresFixture postgres, YjsSidecarFixture yjs) : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    /// <summary>Pages that links point to. They need not exist: the index has no foreign key on the target.</summary>
    internal static readonly Guid[] Targets =
    [
        new("0a1b2c3d-0000-4000-8000-0000000000a0"),
        new("0a1b2c3d-0000-4000-8000-0000000000a1"),
        new("0a1b2c3d-0000-4000-8000-0000000000a2"),
        new("0a1b2c3d-0000-4000-8000-0000000000a3"),
        new("0a1b2c3d-0000-4000-8000-0000000000a4"),
    ];

    /// <summary>A page with a paragraph of text, then one whose text is a link to each given address.</summary>
    internal static byte[] Document(params string[] hrefs)
    {
        var text = new StringBuilder("""{"type":"text","text":"Some text. "}""");
        for (var i = 0; i < hrefs.Length; i++)
        {
            text.Append($$$""",{"type":"text","text":"link {{{i}}} ","marks":[{"type":"link","attrs":{"href":"{{{hrefs[i]}}}"}}]}""");
        }

        return Encoding.UTF8.GetBytes("""{"type":"doc","content":[{"type":"paragraph","content":[""" + text + "]}]}");
    }

    internal static string[] PageAddresses(IEnumerable<Guid> pages) => [.. pages.Select(id => $"/p/{id}")];

    /// <summary>A page whose content links to the given pages.</summary>
    private async Task<Guid> PageLinkingToAsync(params Guid[] pages)
    {
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, [await Codec.BuildAsync(Document(PageAddresses(pages)), default)]);
        return pageId;
    }

    /// <summary>A person changes the page's content: it links to exactly these pages now.</summary>
    private async Task SetLinksAsync(Guid pageId, params Guid[] pages)
    {
        var replacement = await Codec.ReplaceAsync((await Store.LoadAsync(pageId, default)).Inputs, Document(PageAddresses(pages)), default);
        await Store.AppendAsync(pageId, UserId, replacement.Update, default);
    }

    private async Task<Guid[]> IndexedLinksAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return [.. (await db.PageLinks.AsNoTracking().Where(l => l.SourcePageId == pageId).Select(l => l.TargetPageId).ToListAsync()).Order()];
    }

    private async Task<int> LinksIndexedAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.Pages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.LinksIndexed).SingleAsync();
    }

    private async Task CompactAsync(Guid pageId) =>
        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(CompactionResult.Compacted);

    // ---- what a compaction writes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_compaction_puts_the_links_of_the_page_into_the_index_and_marks_the_page_as_indexed()
    {
        var pageId = await PageLinkingToAsync(Targets[1], Targets[0]);
        (await IndexedLinksAsync(pageId)).ShouldBeEmpty();
        (await LinksIndexedAsync(pageId)).ShouldBe(0);

        await CompactAsync(pageId);

        (await IndexedLinksAsync(pageId)).ShouldBe([Targets[0], Targets[1]]);
        (await LinksIndexedAsync(pageId)).ShouldBe(LinkIndex.Version);
    }

    [Fact]
    public async Task A_later_compaction_replaces_the_links_with_those_of_the_new_content_and_a_link_that_was_removed_is_gone()
    {
        var pageId = await PageLinkingToAsync(Targets[0], Targets[1]);
        await CompactAsync(pageId);

        await SetLinksAsync(pageId, Targets[1], Targets[2]);
        await CompactAsync(pageId);

        (await IndexedLinksAsync(pageId)).ShouldBe([Targets[1], Targets[2]]);

        await SetLinksAsync(pageId);
        await CompactAsync(pageId);

        (await IndexedLinksAsync(pageId)).ShouldBeEmpty();
        (await LinksIndexedAsync(pageId)).ShouldBe(LinkIndex.Version);
    }

    [Fact]
    public async Task A_link_to_the_page_itself_is_not_indexed_and_one_to_a_page_that_does_not_exist_is()
    {
        var pageId = await AddPageAsync();
        var gone = Guid.NewGuid();
        await AppendAsync(pageId, [await Codec.BuildAsync(Document($"/p/{pageId}", $"/p/{gone}"), default)]);

        await CompactAsync(pageId);

        (await IndexedLinksAsync(pageId)).ShouldBe([gone]);
    }

    [Fact]
    public async Task A_full_address_of_a_page_counts_and_so_does_the_same_link_twice_only_once()
    {
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, [await Codec.BuildAsync(Document($"https://notes.example.com/p/{Targets[3]}", $"/p/{Targets[3]}", "https://example.com/", $"/p/{Targets[2]}?x=1"), default)]);

        await CompactAsync(pageId);

        (await IndexedLinksAsync(pageId)).ShouldBe([Targets[3]]);
    }

    [Fact]
    public async Task The_links_of_two_pages_are_kept_apart_and_a_page_that_is_deleted_takes_its_rows_with_it()
    {
        var first = await PageLinkingToAsync(Targets[0]);
        var second = await PageLinkingToAsync(Targets[0], Targets[1]);
        await CompactAsync(first);
        await CompactAsync(second);

        (await IndexedLinksAsync(first)).ShouldBe([Targets[0]]);
        (await IndexedLinksAsync(second)).ShouldBe([Targets[0], Targets[1]]);

        await using (var db = CreateContext())
        {
            await db.Pages.Where(p => p.Id == first).ExecuteDeleteAsync();
        }

        (await IndexedLinksAsync(first)).ShouldBeEmpty();
        (await IndexedLinksAsync(second)).ShouldBe([Targets[0], Targets[1]]);
    }

    public static IEnumerable<object[]> Stages() => Enum.GetValues<CompactionStage>().Select(stage => new object[] { stage });

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task A_crash_at_any_stage_leaves_the_index_as_it_was_and_the_next_compaction_writes_it(CompactionStage crashAt)
    {
        var pageId = await PageLinkingToAsync(Targets[0]);
        await CompactAsync(pageId);
        await SetLinksAsync(pageId, Targets[1], Targets[2]);
        Hooks.OnStage = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;

        await Should.ThrowAsync<SimulatedCrash>(() => CreateCompactor().CompactAsync(pageId, default));

        // The snapshot is the old one, so the index is: the links come and go with it, in one transaction.
        (await IndexedLinksAsync(pageId)).ShouldBe([Targets[0]]);
        Hooks.OnStage = null;
        await CompactAsync(pageId);
        (await IndexedLinksAsync(pageId)).ShouldBe([Targets[1], Targets[2]]);
    }

    // ---- the index is always the links of the snapshot -----------------------------------------------------------------

    private const int PerStep = 3;

    [Fact]
    public void The_index_is_always_the_links_of_the_snapshot_whatever_the_edits_compactions_and_crashes()
    {
        var runs = int.TryParse(Environment.GetEnvironmentVariable("INKPACT_PROPERTY_RUNS"), out var configured) && configured > 0 ? configured : 40;
        var config = Config.QuickThrowOnFailure.WithMaxTest(runs).WithEndSize(60).WithQuietOnSuccess(true);
        var step = Gen.ArrayOf(Gen.Choose(0, 9999), PerStep);
        var schedule = Gen.Sized(size => Gen.Choose(1, Math.Clamp(size / 4 + 2, 2, 14)).SelectMany(count => Gen.ListOf(step, count)))
            .Select(steps => steps.SelectMany(s => s).ToArray());

        Check.One(config, Prop.ForAll(Arb.From(schedule), numbers => RunScheduleAsync(numbers).GetAwaiter().GetResult()));
    }

    private async Task<bool> RunScheduleAsync(int[] numbers)
    {
        var pageId = await AddPageAsync();
        var stages = Enum.GetValues<CompactionStage>();
        // The first content: nobody has to edit it to have links.
        await AppendAsync(pageId, [await Codec.BuildAsync(Document(), default)]);

        for (var i = 0; i < numbers.Length / PerStep; i++)
        {
            var (kind, a, b) = (numbers[i * PerStep] % 4, numbers[i * PerStep + 1], numbers[i * PerStep + 2]);
            Options.MaxUpdatesPerCompaction = b % 3 == 0 ? 1 + b % 4 : 5_000;
            Hooks.OnStage = null;
            switch (kind)
            {
                case 0:
                case 1:
                    // A person changes the links: each of the five targets is in or out, by a bit of the number.
                    await SetLinksAsync(pageId, [.. Targets.Where((_, bit) => (a >> bit & 1) == 1)]);
                    break;
                case 2:
                    await CreateCompactor().CompactAsync(pageId, default);
                    break;
                default:
                    var crashAt = stages[a % stages.Length];
                    Hooks.OnStage = stage => stage == crashAt ? throw new SimulatedCrash(stage) : Task.CompletedTask;
                    try
                    {
                        await CreateCompactor().CompactAsync(pageId, default);
                    }
                    catch (SimulatedCrash)
                    {
                        // The crash is the point.
                    }

                    break;
            }

            Hooks.OnStage = null;
            await CheckIndexIsTheSnapshotsAsync(pageId);
        }

        // Compacted to the end, the index is the links of the page as the whole of its updates make it.
        Options.MaxUpdatesPerCompaction = 5_000;
        while (await CreateCompactor().CompactAsync(pageId, default) == CompactionResult.Compacted)
        {
        }

        var replayed = (await Codec.CompactAsync((await Store.LoadAsync(pageId, default)).Inputs, default)).Links;
        (await IndexedLinksAsync(pageId)).ShouldBe([.. replayed.Order()]);
        return true;
    }

    private async Task CheckIndexIsTheSnapshotsAsync(Guid pageId)
    {
        var snapshot = (await ReadStoredAsync(pageId)).Snapshot;
        var expected = snapshot is null ? [] : (await Codec.CompactAsync([snapshot.State], default)).Links.Order().ToArray();
        (await IndexedLinksAsync(pageId)).ShouldBe(expected);
    }
}
