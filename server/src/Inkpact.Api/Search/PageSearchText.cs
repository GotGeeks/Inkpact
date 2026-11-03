using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Search;

/// <summary>
/// Keeps <c>pages.tag_text</c>, the names of a page's tags, which the page's search vector is made from. Whatever changes
/// which tags a page has, or what a tag is called, calls this afterwards, in the same transaction (the import now, the tag
/// interface of run B.2 later). Tags are in tables of their own, which a generated column cannot read.
/// </summary>
public static class PageSearchText
{
    public static Task RefreshAsync(InkpactDbContext db, Guid pageId, CancellationToken cancellationToken) =>
        RefreshAsync(db, [pageId], cancellationToken);

    public static async Task RefreshAsync(InkpactDbContext db, IReadOnlyCollection<Guid> pageIds, CancellationToken cancellationToken)
    {
        if (pageIds.Count == 0)
        {
            return;
        }

        var ids = pageIds.ToArray();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE pages p SET tag_text = COALESCE((
                SELECT string_agg(g.name, ' ' ORDER BY g.normalized_name COLLATE "C", g.id)
                FROM page_tags pt JOIN tags g ON g.id = pt.tag_id
                WHERE pt.page_id = p.id), '')
            WHERE p.id = ANY({ids})
            """, cancellationToken);
    }

    /// <summary>Every page that has the tag, for when the tag has been renamed.</summary>
    public static async Task RefreshPagesOfTagAsync(InkpactDbContext db, Guid tagId, CancellationToken cancellationToken) =>
        await RefreshAsync(
            db, await db.PageTags.AsNoTracking().Where(t => t.TagId == tagId).Select(t => t.PageId).ToListAsync(cancellationToken), cancellationToken);
}
