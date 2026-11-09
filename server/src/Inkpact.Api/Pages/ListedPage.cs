using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Pages;

/// <summary>A page in a list of pages (backlinks, a tag's pages): its id and title, and its parent's title so that pages with one title can be told apart.</summary>
public sealed record ListedPage(Guid Id, string Title, string? ParentTitle);

public static class ListedPages
{
    /// <summary>The pages in the order given, each with its parent's title, read in one query for all of them.</summary>
    public static async Task<List<ListedPage>> WithParentTitlesAsync(
        InkpactDbContext db, IReadOnlyList<(Guid Id, string Title, Guid? ParentId)> pages, CancellationToken cancellationToken)
    {
        Guid[] parentIds = [.. pages.Where(p => p.ParentId is not null).Select(p => p.ParentId!.Value).Distinct()];
        var parentTitles = parentIds.Length == 0
            ? []
            : await db.Pages.AsNoTracking()
                .Where(p => parentIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);

        return [.. pages.Select(p => new ListedPage(p.Id, p.Title, p.ParentId is { } parent ? parentTitles.GetValueOrDefault(parent) : null))];
    }
}
