using Inkpact.Api.Search;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Tests.Search;

/// <summary>The words a person types are found once the page has been compacted, which is when its text is written.</summary>
public class SearchCompactionTests(PostgresFixture postgres, YjsSidecarFixture yjs) : CompactionTestBase(postgres, yjs), IClassFixture<YjsSidecarFixture>
{
    private async Task<Guid[]> FindAsync(string text)
    {
        await using var db = CreateContext();
        return [.. (await new SearchService(db).SearchAsync(WorkspaceId, text)).Results.Select(r => r.Id)];
    }

    [Fact]
    public async Task A_word_in_a_page_is_found_after_the_page_is_compacted_and_not_before()
    {
        var pageId = await AddPageAsync();
        await AppendAsync(pageId, EditStreams.Get(0, 1));
        (await FindAsync("boodschappen")).ShouldBeEmpty();

        await CreateCompactor().CompactAsync(pageId, default);

        (await FindAsync("boodschappen")).ShouldBe([pageId]);
        (await FindAsync("kaas")).ShouldBe([pageId]);
    }

    [Fact]
    public async Task The_page_is_still_found_by_its_title_and_its_tags_after_a_compaction()
    {
        var pageId = await AddPageAsync();
        await using (var db = CreateContext())
        {
            var page = await db.Pages.FindAsync(pageId);
            page!.Title = "Weekend";
            page.TagText = "familie";
            await db.SaveChangesAsync();
        }

        await AppendAsync(pageId, EditStreams.Get(0, 1));
        await CreateCompactor().CompactAsync(pageId, default);

        (await FindAsync("weekend")).ShouldBe([pageId]);
        (await FindAsync("famil")).ShouldBe([pageId]);
        (await FindAsync("kaas")).ShouldBe([pageId]);
    }

    [Fact]
    public async Task A_compaction_that_does_not_change_the_text_leaves_the_page_row_and_its_vectors_alone()
    {
        var stream = EditStreams.Get(0, 1);
        var pageId = await AddPageAsync();
        var text = (await Codec.CompactAsync(stream, default)).Text.Replace("\0", "");
        await using (var db = CreateContext())
        {
            // A page that has been compacted before: its links are indexed, so nothing is left for this compaction to write
            // on the row (the first compaction of a page also marks it as indexed, once).
            await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ContentText, text)
                .SetProperty(p => p.LinksIndexed, Inkpact.Api.Links.LinkIndex.Version));
        }

        var before = await RowVersionAsync(pageId);
        await AppendAsync(pageId, stream);

        (await CreateCompactor().CompactAsync(pageId, default)).ShouldBe(Inkpact.Api.Compaction.CompactionResult.Compacted);

        (await RowVersionAsync(pageId)).ShouldBe(before);
        (await FindAsync("kaas")).ShouldBe([pageId]);
    }

    // Postgres' transaction id of the row's current version: it changes with every update of the row.
    private async Task<string> RowVersionAsync(Guid pageId)
    {
        await using var db = CreateContext();
        return await db.Database.SqlQuery<string>($"SELECT xmin::text AS \"Value\" FROM pages WHERE id = {pageId}").SingleAsync();
    }
}
