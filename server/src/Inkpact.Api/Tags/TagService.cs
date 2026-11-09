using Inkpact.Api.Pages;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Inkpact.Api.Tags;

/// <summary>A tag, as it is used on a page or offered while typing one.</summary>
public sealed record TagDto(Guid Id, string Name);

/// <summary>A workspace's tag, with the number of live pages that have it.</summary>
public sealed record WorkspaceTagDto(Guid Id, string Name, int PageCount);

/// <summary>The live pages of a tag, at most <see cref="TagService.MaxTagPages"/> of them; <c>More</c> when there are more.</summary>
public sealed record TagPagesDto(string TagName, IReadOnlyList<ListedPage> Items, bool More);

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
    public const int MaxNameLength = TagNames.MaxLength;

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
    /// Adds and removes tags on a page, as a set: removes first, then adds. A name is cleaned by the import's rule
    /// (<see cref="TagNames"/>) and matched to an existing tag of the workspace by its lower case, with that tag's own
    /// spelling kept; otherwise a tag is made with the name as given. Adding a tag the page has, or removing one it does
    /// not, does nothing. The page row is locked for the change, so two requests on one page run one after the other. Two
    /// pages making the same new tag at once meet the unique index, and a rename may drop a tag this request just found;
    /// either way the request is run again once. Answers with the page's tags as they now are.
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
            if (CheckName(raw, "add", out var clean) is { } error)
            {
                return error;
            }

            cleanedAdd.Add((clean, TagNames.Normalize(clean)));
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
                catch (DbUpdateException ex) when (attempt == 0 && IsRace(ex))
                {
                    // Another page made the same new tag at the same moment, or a rename dropped the unused tag this found:
                    // the next attempt reads the tags again.
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

    /// <summary>
    /// The live pages of a tag, by title (case ignored, as the backlinks), at most <see cref="MaxTagPages"/>, sorted and
    /// cut in the database; <c>not_found</c> for a tag of another workspace or none at all.
    /// </summary>
    public async Task<TagResult<TagPagesDto>> GetTagPagesAsync(Guid workspaceId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tagId && t.WorkspaceId == workspaceId, cancellationToken);
        if (tag is null)
        {
            return new TagError(StatusCodes.Status404NotFound, "not_found");
        }

        // One more than fits, to know whether there are more. upper() in the "C" collation is what OrdinalIgnoreCase does.
        var rows = await db.Database.SqlQuery<TagPageRow>($"""
            SELECT p.id AS "Id", p.title AS "Title", p.parent_id AS "ParentId"
            FROM page_tags pt
            JOIN pages p ON p.id = pt.page_id
            JOIN live_pages({workspaceId}) live ON live.page_id = p.id
            WHERE pt.tag_id = {tagId}
            ORDER BY upper(p.title) COLLATE "C", p.id
            LIMIT {MaxTagPages + 1}
            """).ToListAsync(cancellationToken);

        var more = rows.Count > MaxTagPages;
        var pages = await ListedPages.WithParentTitlesAsync(
            db, [.. rows.Take(MaxTagPages).Select(r => (r.Id, r.Title, r.ParentId))], cancellationToken);
        return new TagPagesDto(tag.Name, pages, more);
    }

    /// <summary>
    /// Renames a tag. A change of case only is allowed. Another tag with the name is a conflict, unless no page has it at
    /// all (not even one in the trash): such a tag is only kept so that its name reuses it, and the renamed tag takes over
    /// that role, so it goes. A tag that only pages in the trash have is a conflict of its own (<c>tag_name_taken_in_trash</c>),
    /// since the Tags view does not show it. A rename that meets another request making the same name is a conflict too.
    /// </summary>
    public async Task<TagResult<TagDto>> RenameTagAsync(Guid workspaceId, Guid tagId, string? name, CancellationToken cancellationToken = default)
    {
        if (CheckName(name, "name", out var clean) is { } error)
        {
            return error;
        }

        var normalized = TagNames.Normalize(clean);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId && t.WorkspaceId == workspaceId, cancellationToken);
        if (tag is null)
        {
            return new TagError(StatusCodes.Status404NotFound, "not_found");
        }

        if (tag.NormalizedName != normalized)
        {
            var other = await db.Tags.AsNoTracking()
                .Where(t => t.WorkspaceId == workspaceId && t.Id != tagId && t.NormalizedName == normalized)
                .Select(t => (Guid?)t.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (other is { } otherId)
            {
                if (await db.PageTags.AnyAsync(pt => pt.TagId == otherId, cancellationToken))
                {
                    var onLivePage = await db.Database.SqlQuery<bool>($"""
                        select exists(
                            select 1 from page_tags pt join live_pages({workspaceId}) live on live.page_id = pt.page_id
                            where pt.tag_id = {otherId}) as "Value"
                        """).SingleAsync(cancellationToken);
                    return new TagError(StatusCodes.Status409Conflict, onLivePage ? "tag_name_taken" : "tag_name_taken_in_trash", "name");
                }

                // Deleted first, in its own statement, so the unique index never sees both names at once.
                await db.Tags.Where(t => t.Id == otherId).ExecuteDeleteAsync(cancellationToken);
            }
        }

        tag.Name = clean;
        tag.NormalizedName = normalized;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsRace(ex))
        {
            // Another request made a tag of this name since it was looked for.
            return new TagError(StatusCodes.Status409Conflict, "tag_name_taken", "name");
        }

        await transaction.CommitAsync(cancellationToken);
        return new TagDto(tag.Id, tag.Name);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>The name cleaned by <see cref="TagNames.Clean"/>, or the reason it cannot be one.</summary>
    private static TagError? CheckName(string? raw, string field, out string clean)
    {
        clean = TagNames.Clean(raw);
        if (clean.Length == 0)
        {
            return new TagError(StatusCodes.Status400BadRequest, "tag_name_empty", field);
        }

        return clean.Length > MaxNameLength ? new TagError(StatusCodes.Status400BadRequest, "tag_name_too_long", field) : null;
    }

    /// <summary>A unique name taken, or a tag that went away, by another request at the same moment.</summary>
    private static bool IsRace(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation };

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
