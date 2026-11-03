using Inkpact.Api.Search;
using Inkpact.Api.Tests.Compaction;
using Inkpact.Api.Tests.Crdt;
using Inkpact.Api.Tests.Testing;

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
}
