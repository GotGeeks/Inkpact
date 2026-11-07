using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Links;

/// <summary>
/// The index of the links between pages (<c>page_links</c>, run B.1). The sidecar finds the links of a page's content; this
/// puts them into the table, in the transaction of whoever found them: the compactor with the snapshot, or the job that
/// indexes the pages that exist from before.
/// </summary>
public static class LinkIndex
{
    /// <summary>
    /// The version of the extraction that finds the links (the sidecar's <c>pageLinks</c>). A page whose
    /// <c>links_indexed</c> is lower is indexed again by the job at start-up, so a change to what counts as a link is
    /// one increase of this number.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Makes the links of <paramref name="sourcePageId"/> in the index exactly <paramref name="targets"/>, and the page's
    /// <c>links_indexed</c> the current version. Only what changed is written. A page that links to itself has no row for it:
    /// its backlinks would show itself. The caller's transaction decides whether it commits.
    /// </summary>
    public static async Task ReplaceAsync(
        InkpactDbContext db, Guid sourcePageId, IEnumerable<Guid> targets, CancellationToken cancellationToken)
    {
        var wanted = targets.Where(target => target != sourcePageId).ToHashSet();
        var existing = await db.PageLinks.AsNoTracking()
            .Where(l => l.SourcePageId == sourcePageId)
            .Select(l => l.TargetPageId)
            .ToListAsync(cancellationToken);

        var gone = existing.Where(target => !wanted.Contains(target)).ToList();
        if (gone.Count > 0)
        {
            await db.PageLinks.Where(l => l.SourcePageId == sourcePageId && gone.Contains(l.TargetPageId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        var known = existing.ToHashSet();
        var added = wanted.Where(target => !known.Contains(target)).ToList();
        if (added.Count > 0)
        {
            db.PageLinks.AddRange(added.Select(target => new PageLink { SourcePageId = sourcePageId, TargetPageId = target }));
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Pages.Where(p => p.Id == sourcePageId && p.LinksIndexed != Version)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.LinksIndexed, Version), cancellationToken);
    }
}
