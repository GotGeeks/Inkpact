using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inkpact.Api.Tags;

/// <summary>A tag, as it is used on a page or offered while typing one.</summary>
public sealed record TagDto(Guid Id, string Name);

/// <summary>A workspace's tag, with the number of live pages that have it.</summary>
public sealed record WorkspaceTagDto(Guid Id, string Name, int PageCount);

/// <summary>A page that has a tag: its id and title, and its parent's title so pages with one title can be told apart.</summary>
public sealed record TagPageDto(Guid Id, string Title, string? ParentTitle);

/// <summary>The live pages of a tag, at most <see cref="TagService.MaxTagPages"/> of them; <c>More</c> when there are more.</summary>
public sealed record TagPagesDto(string TagName, IReadOnlyList<TagPageDto> Items, bool More);

/// <summary>An operation that was refused: the status, the code the client translates, and the field it concerns.</summary>
public sealed record TagError(int Status, string Code, string? Field = null);

/// <summary>Either the result, or the reason there is none.</summary>
public readonly record struct TagResult<T>(T? Value, TagError? Error)
{
    public bool Succeeded => Error is null;

    public static implicit operator TagResult<T>(T value) => new(value, null);

    public static implicit operator TagResult<T>(TagError error) => new(default, error);
}

/// <summary>
/// Tags on pages (run B.2): a flat set per workspace, named by the Joplin import and from here. A page's tags are an
/// add-and-remove set operation, so two changes of one page at once both land; the search text (<c>tag_text</c>) and
/// the export follow with no code here, kept by the triggers of the search migration.
/// </summary>
public sealed class TagService(InkpactDbContext db, TimeProvider time)
{
    public const int MaxNameLength = 200;

    /// <summary>Names and ids together, in one request to change a page's tags.</summary>
    public const int MaxNamesPerRequest = 100;

    public const int MaxTagPages = 200;

    // ---- a page's tags -------------------------------------------------------------------------------------------

    /// <summary>A page's tags, by their lower-case name.</summary>
    public async Task<IReadOnlyList<TagDto>> GetPageTagsAsync(Guid pageId, CancellationToken cancellationToken = default) =>
        await db.PageTags.AsNoTracking()
            .Where(pt => pt.PageId == pageId)
            .Join(db.Tags.AsNoTracking(), pt => pt.TagId, t => t.Id, (_, t) => t)
            .OrderBy(t => t.NormalizedName)
            .Select(t => new TagDto(t.Id, t.Name))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Adds and removes tags on a page, as a set: removes first, then adds. A name is matched to an existing tag of the
    /// workspace by its lower case, with that tag's own spelling kept; otherwise a tag is made with the name as given.
    /// Adding a tag the page has, or removing one it does not, does nothing. The page row is locked for the change, so
    /// two requests on one page run one after the other; two workspaces' pages making the same new tag at once is the
    /// unique index's race, tried again once. Answers with the page's tags as they now are.
    /// </summary>
    public async Task<TagResult<IReadOnlyList<TagDto>>> ChangeTagsAsync(
        Guid pageId, IReadOnlyList<string>? addNames, IReadOnlyList<Guid>? removeIds, CancellationToken cancellationToken = default)
    {
        var add = addNames ?? [];
        var remove = removeIds ?? [];
        if (add.Count + remove.Count > MaxNamesPerRequest)
        {
            return new TagError(StatusCodes.Status400BadRequest, "too_many_tags");
        }

        var cleanedAdd = new List<(string Name, string Normalized)>();
        foreach (var raw in add)
        {
            var clean = CleanName(raw);
            if (clean is null)
            {
                return new TagError(StatusCodes.Status400BadRequest, "tag_name_empty", "add");
            }

            if (clean.Length > MaxNameLength)
            {
                return new TagError(StatusCodes.Status400BadRequest, "tag_name_too_long", "add");
            }

            cleanedAdd.Add((clean, clean.ToLowerInvariant()));
        }

        for (var attempt = 0; ; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // For update: two changes of this page's tags run one after the other, each on the set the other left.
            var page = (await db.Pages
                .FromSql($"select * from pages where id = {pageId} for update")
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (page is null)
            {
                return new TagError(StatusCodes.Status404NotFound, "not_found");
            }

            // The tree's own rule: metadata is read-only on a page in the trash, or below one. The same function the
            // tree and search read, so this never disagrees with what the page view shows.
            var alive = await db.Database
                .SqlQuery<bool>($"select exists(select 1 from live_pages({page.WorkspaceId}) where page_id = {pageId}) as \"Value\"")
                .SingleAsync(cancellationToken);
            if (!alive)
            {
                return new TagError(StatusCodes.Status409Conflict, "page_in_trash");
            }

            if (remove.Count > 0)
            {
                await db.PageTags.Where(pt => pt.PageId == pageId && remove.Contains(pt.TagId)).ExecuteDeleteAsync(cancellationToken);
            }

            if (cleanedAdd.Count > 0)
            {
                var normalizedNames = cleanedAdd.Select(a => a.Normalized).Distinct().ToList();
                var existingTags = await db.Tags
                    .Where(t => t.WorkspaceId == page.WorkspaceId && normalizedNames.Contains(t.NormalizedName))
                    .ToDictionaryAsync(t => t.NormalizedName, cancellationToken);
                var pageTagIds = (await db.PageTags.Where(pt => pt.PageId == pageId).Select(pt => pt.TagId).ToListAsync(cancellationToken)).ToHashSet();

                foreach (var (name, normalized) in cleanedAdd.DistinctBy(a => a.Normalized))
                {
                    if (!existingTags.TryGetValue(normalized, out var tag))
                    {
                        tag = new Tag
                        {
                            WorkspaceId = page.WorkspaceId,
                            Name = name,
                            NormalizedName = normalized,
                            CreatedAt = time.GetUtcNow(),
                        };
                        db.Tags.Add(tag);
                        existingTags[normalized] = tag;
                    }

                    if (pageTagIds.Add(tag.Id))
                    {
                        db.PageTags.Add(new PageTag { PageId = pageId, TagId = tag.Id });
                    }
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
                {
                    // Another page made the same new tag at the same moment: the next attempt finds it instead of making
                    // a second one.
                    db.ChangeTracker.Clear();
                    continue;
                }
            }

            await transaction.CommitAsync(cancellationToken);
            var tags = await GetPageTagsAsync(pageId, cancellationToken);
            return new TagResult<IReadOnlyList<TagDto>>(tags, null);
        }
    }

    // ---- the workspace's tags -------------------------------------------------------------------------------------

    /// <summary>
    /// The workspace's tags that are on at least one live page, with that count, by their lower-case name. A tag on no
    /// live page is left out: it is still there for a name typed again, which uses it rather than making a duplicate.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceTagDto>> GetWorkspaceTagsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database.SqlQuery<WorkspaceTagRow>($"""
            SELECT t.id AS "Id", t.name AS "Name", t.normalized_name AS "NormalizedName", count(pt.page_id) AS "PageCount"
            FROM tags t
            JOIN page_tags pt ON pt.tag_id = t.id
            JOIN live_pages({workspaceId}) live ON live.page_id = pt.page_id
            WHERE t.workspace_id = {workspaceId}
            GROUP BY t.id, t.name, t.normalized_name
            ORDER BY t.normalized_name
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(r => new WorkspaceTagDto(r.Id, r.Name, (int)r.PageCount))];
    }

    /// <summary>The live pages of a tag, by title; <c>not_found</c> for a tag of another workspace or none at all.</summary>
    public async Task<TagResult<TagPagesDto>> GetTagPagesAsync(Guid workspaceId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tagId && t.WorkspaceId == workspaceId, cancellationToken);
        if (tag is null)
        {
            return new TagError(StatusCodes.Status404NotFound, "not_found");
        }

        var rows = await db.Database.SqlQuery<TagPageRow>($"""
            SELECT p.id AS "Id", p.title AS "Title", p.parent_id AS "ParentId"
            FROM page_tags pt
            JOIN pages p ON p.id = pt.page_id
            JOIN live_pages({workspaceId}) live ON live.page_id = p.id
            WHERE pt.tag_id = {tagId}
            """).ToListAsync(cancellationToken);

        var ordered = rows.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
        var more = ordered.Count > MaxTagPages;
        var page = ordered.Take(MaxTagPages).ToList();

        Guid[] parentIds = [.. page.Where(r => r.ParentId is not null).Select(r => r.ParentId!.Value).Distinct()];
        var parentTitles = await db.Pages.AsNoTracking()
            .Where(p => parentIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Title, cancellationToken);

        return new TagPagesDto(
            tag.Name,
            [.. page.Select(r => new TagPageDto(r.Id, r.Title, r.ParentId is { } parent ? parentTitles.GetValueOrDefault(parent) : null))],
            more);
    }

    /// <summary>Renames a tag; a change of case only is allowed, another spelling that collides with another tag is not.</summary>
    public async Task<TagResult<TagDto>> RenameTagAsync(Guid workspaceId, Guid tagId, string? name, CancellationToken cancellationToken = default)
    {
        var clean = CleanName(name);
        if (clean is null)
        {
            return new TagError(StatusCodes.Status400BadRequest, "tag_name_empty", "name");
        }

        if (clean.Length > MaxNameLength)
        {
            return new TagError(StatusCodes.Status400BadRequest, "tag_name_too_long", "name");
        }

        var normalized = clean.ToLowerInvariant();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId && t.WorkspaceId == workspaceId, cancellationToken);
        if (tag is null)
        {
            return new TagError(StatusCodes.Status404NotFound, "not_found");
        }

        var taken = await db.Tags.AsNoTracking()
            .AnyAsync(t => t.WorkspaceId == workspaceId && t.Id != tagId && t.NormalizedName == normalized, cancellationToken);
        if (taken)
        {
            return new TagError(StatusCodes.Status409Conflict, "tag_name_taken", "name");
        }

        tag.Name = clean;
        tag.NormalizedName = normalized;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TagDto(tag.Id, tag.Name);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>Trimmed, with inner white space made one space; null when that leaves nothing.</summary>
    private static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    // What the raw queries return; the names match the aliases in the SQL.
    private sealed class WorkspaceTagRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string NormalizedName { get; set; } = "";

        public long PageCount { get; set; }
    }

    private sealed class TagPageRow
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = "";

        public Guid? ParentId { get; set; }
    }
}
