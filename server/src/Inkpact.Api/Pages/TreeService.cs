using System.Text.Json.Serialization;
using Inkpact.Data;
using Inkpact.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Pages;

/// <summary>A page as the tree shows it.</summary>
public sealed record PageDto(
    Guid Id,
    Guid? ParentId,
    string Title,
    string SortKey,
    int Version,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? DeletedAt = null);

/// <summary>The live tree of a workspace, flat and in tree order (a parent before its children, siblings by key).</summary>
public sealed record TreeDto(long TreeVersion, IReadOnlyList<PageDto> Pages);

/// <summary>What a change did. <c>Conflict</c> means the page had changed since the version the client sent.</summary>
public sealed record PageChange(
    PageDto Page,
    bool Conflict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RestoredTo = null);

/// <summary>Where a page is wanted among its new siblings: between two of them, or at the end when neither is named.</summary>
public sealed record Position(Guid? ParentId, Guid? AfterId, Guid? BeforeId);

/// <summary>An operation that was refused: the status, the code the client translates, and the field it concerns.</summary>
public sealed record TreeError(int Status, string Code, string? Field = null);

/// <summary>Either the result, or the reason there is none.</summary>
public readonly record struct TreeResult<T>(T? Value, TreeError? Error)
{
    public bool Succeeded => Error is null;

    public static implicit operator TreeResult<T>(T value) => new(value, null);

    public static implicit operator TreeResult<T>(TreeError error) => new(default, error);
}

/// <summary>
/// Every change to the page tree. Each one runs in its own transaction and starts by raising the workspace's tree
/// version, which locks the workspace row, so the changes to one workspace are applied one at a time and each one is
/// checked against the state the one before it left. That is what keeps two moves that are fine on their own from
/// making a cycle together.
/// </summary>
public sealed class TreeService(InkpactDbContext db, TimeProvider time, ILogger<TreeService> logger)
{
    public const int MaxTitleLength = 500;

    // ---- reading -----------------------------------------------------------------------------------------------------

    /// <summary>The pages that are not in the trash, nor below something that is.</summary>
    public async Task<TreeDto> GetTreeAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        // Reaches the pages from the top, and stops at trashed ones, so their children are not reached either.
        var rows = await db.Database.SqlQuery<PageRow>($"""
            WITH RECURSIVE live AS (
                SELECT id, parent_id, title, sort_key, version, updated_at
                FROM pages
                WHERE workspace_id = {workspaceId} AND parent_id IS NULL AND deleted_at IS NULL
              UNION ALL
                SELECT c.id, c.parent_id, c.title, c.sort_key, c.version, c.updated_at
                FROM pages c JOIN live p ON c.parent_id = p.id
                WHERE c.deleted_at IS NULL
            )
            SELECT id AS "Id", parent_id AS "ParentId", title AS "Title", sort_key AS "SortKey",
                   version AS "Version", updated_at AS "UpdatedAt", NULL::timestamptz AS "DeletedAt"
            FROM live
            """).ToListAsync(cancellationToken);
        var treeVersion = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => w.TreeVersion)
            .SingleOrDefaultAsync(cancellationToken);

        return new TreeDto(treeVersion, InTreeOrder(rows.Select(r => r.ToDto())));
    }

    /// <summary>The pages a person can restore: in the trash themselves, with nothing above them in the trash.</summary>
    public async Task<IReadOnlyList<PageDto>> GetTrashAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database.SqlQuery<PageRow>($"""
            SELECT p.id AS "Id", p.parent_id AS "ParentId", p.title AS "Title", p.sort_key AS "SortKey",
                   p.version AS "Version", p.updated_at AS "UpdatedAt", p.deleted_at AS "DeletedAt"
            FROM pages p
            WHERE p.workspace_id = {workspaceId} AND p.deleted_at IS NOT NULL
              AND NOT EXISTS (
                WITH RECURSIVE above AS (
                    SELECT id, parent_id, deleted_at FROM pages WHERE id = p.parent_id
                  UNION
                    SELECT a.id, a.parent_id, a.deleted_at FROM pages a JOIN above c ON a.id = c.parent_id
                )
                SELECT 1 FROM above WHERE deleted_at IS NOT NULL)
            ORDER BY p.deleted_at DESC, p.id
            """).ToListAsync(cancellationToken);
        return rows.Select(r => r.ToDto()).ToList();
    }

    /// <summary>One page, and whether it is in the trash, itself or through something above it.</summary>
    public async Task<(PageDto Page, bool InTrash)?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await db.Pages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        var inTrash = (await GetChainAsync(pageId, cancellationToken)).Any(link => link.DeletedAt is not null);
        return (ToDto(page), inTrash);
    }

    /// <summary>Parents before children, and siblings by key, compared as bytes the way the database sorts them.</summary>
    internal static List<PageDto> InTreeOrder(IEnumerable<PageDto> pages)
    {
        var all = pages.ToList();
        var byParent = all.ToLookup(p => p.ParentId);
        var ids = all.Select(p => p.Id).ToHashSet();
        var ordered = new List<PageDto>(all.Count);

        void Walk(Guid? parent)
        {
            foreach (var page in byParent[parent].OrderBy(p => p.SortKey, StringComparer.Ordinal))
            {
                ordered.Add(page);
                Walk(page.Id);
            }
        }

        Walk(null);
        // A page whose parent is not in the list cannot be reached from the top; none should exist.
        ordered.AddRange(all.Where(p => p.ParentId is not null && !ids.Contains(p.ParentId.Value)));
        return ordered;
    }

    // ---- changing ----------------------------------------------------------------------------------------------------

    public async Task<TreeResult<PageChange>> CreateAsync(Guid workspaceId, string? title, Position position, CancellationToken cancellationToken = default)
    {
        var cleanTitle = CleanTitle(title);
        if (cleanTitle is null)
        {
            return new TreeError(StatusCodes.Status400BadRequest, "title_invalid", "title");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockWorkspaceAsync(workspaceId, cancellationToken))
        {
            return new TreeError(StatusCodes.Status404NotFound, "not_found");
        }

        if (await CheckParentAsync(workspaceId, position.ParentId, cancellationToken) is { } parentError)
        {
            return parentError;
        }

        var key = await ResolveKeyAsync(workspaceId, movedPageId: null, position, cancellationToken);
        if (key.Error is not null)
        {
            return key.Error;
        }

        var now = time.GetUtcNow();
        var page = new Page
        {
            WorkspaceId = workspaceId,
            ParentId = position.ParentId,
            Title = cleanTitle,
            SortKey = key.Value!,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PageChange(ToDto(page), Conflict: false);
    }

    public async Task<TreeResult<PageChange>> RenameAsync(Guid pageId, string? title, int? expectedVersion, CancellationToken cancellationToken = default)
    {
        var cleanTitle = CleanTitle(title);
        if (cleanTitle is null)
        {
            return new TreeError(StatusCodes.Status400BadRequest, "title_invalid", "title");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = await LockAndLoadAsync(pageId, cancellationToken);
        if (page is null)
        {
            return new TreeError(StatusCodes.Status404NotFound, "not_found");
        }

        if (await IsInTrashAsync(pageId, cancellationToken))
        {
            return new TreeError(StatusCodes.Status409Conflict, "page_in_trash");
        }

        var conflict = NoteConflict(page, "rename", expectedVersion);
        page.Title = cleanTitle;
        Touch(page);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PageChange(ToDto(page), conflict);
    }

    public async Task<TreeResult<PageChange>> MoveAsync(Guid pageId, Position position, int? expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = await LockAndLoadAsync(pageId, cancellationToken);
        if (page is null)
        {
            return new TreeError(StatusCodes.Status404NotFound, "not_found");
        }

        if (await IsInTrashAsync(pageId, cancellationToken))
        {
            return new TreeError(StatusCodes.Status409Conflict, "page_in_trash");
        }

        if (await CheckParentAsync(page.WorkspaceId, position.ParentId, cancellationToken) is { } parentError)
        {
            return parentError;
        }

        // The new parent may not be the page, nor anything below it: that would cut the page off from the tree.
        if (position.ParentId is { } parentId
            && (await GetChainAsync(parentId, cancellationToken)).Any(link => link.Id == pageId))
        {
            return new TreeError(StatusCodes.Status409Conflict, "move_cycle", "parentId");
        }

        var key = await ResolveKeyAsync(page.WorkspaceId, pageId, position, cancellationToken);
        if (key.Error is not null)
        {
            return key.Error;
        }

        var conflict = NoteConflict(page, "move", expectedVersion);
        page.ParentId = position.ParentId;
        page.SortKey = key.Value!;
        Touch(page);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PageChange(ToDto(page), conflict);
    }

    /// <summary>Puts a page in the trash. Its children are not touched: they are in the trash because the page is.</summary>
    public async Task<TreeResult<PageChange>> TrashAsync(Guid pageId, int? expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = await LockAndLoadAsync(pageId, cancellationToken);
        if (page is null)
        {
            return new TreeError(StatusCodes.Status404NotFound, "not_found");
        }

        if (page.DeletedAt is null && !await IsInTrashAsync(pageId, cancellationToken))
        {
            var conflict = NoteConflict(page, "trash", expectedVersion);
            page.DeletedAt = time.GetUtcNow();
            Touch(page);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PageChange(ToDto(page), conflict);
        }

        // Already in the trash, itself or through a parent: nothing to do, and the answer says where things stand.
        await transaction.CommitAsync(cancellationToken);
        return new PageChange(ToDto(page), Conflict: false);
    }

    /// <summary>
    /// Takes a page out of the trash, back to where it was when that place is still valid, else to the end of the top level.
    /// </summary>
    public async Task<TreeResult<PageChange>> RestoreAsync(Guid pageId, int? expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var page = await LockAndLoadAsync(pageId, cancellationToken);
        if (page is null)
        {
            return new TreeError(StatusCodes.Status404NotFound, "not_found");
        }

        if (page.DeletedAt is null)
        {
            // Hidden only because something above it is in the trash: that is the thing to restore.
            if (await IsInTrashAsync(pageId, cancellationToken))
            {
                return new TreeError(StatusCodes.Status409Conflict, "restore_the_parent");
            }

            await transaction.CommitAsync(cancellationToken);
            return new PageChange(ToDto(page), Conflict: false);
        }

        var conflict = NoteConflict(page, "restore", expectedVersion);
        var restoredTo = "original";
        var parentUsable = page.ParentId is null
            || !(await GetChainAsync(page.ParentId.Value, cancellationToken)).Any(link => link.DeletedAt is not null);

        if (parentUsable)
        {
            // Where it was, if its key is still free; otherwise just after the nearest key below it.
            var taken = await LiveSiblings(page.WorkspaceId, page.ParentId, pageId).AnyAsync(p => p.SortKey == page.SortKey, cancellationToken);
            if (taken)
            {
                var below = await LiveSiblings(page.WorkspaceId, page.ParentId, pageId)
                    .Where(p => string.Compare(p.SortKey, page.SortKey) < 0)
                    .OrderByDescending(p => p.SortKey)
                    .Select(p => p.SortKey)
                    .FirstOrDefaultAsync(cancellationToken);
                var above = await NextKeyAfterAsync(page.WorkspaceId, page.ParentId, pageId, below, cancellationToken);
                page.SortKey = FractionalIndex.KeyBetween(below, above);
            }
        }
        else
        {
            restoredTo = "root";
            var last = await LastKeyAsync(page.WorkspaceId, null, pageId, cancellationToken);
            page.ParentId = null;
            page.SortKey = FractionalIndex.KeyBetween(last, null);
        }

        page.DeletedAt = null;
        Touch(page);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PageChange(ToDto(page), conflict, restoredTo);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>Raises the workspace's tree version. That locks its row until the transaction ends. False when there is no such workspace.</summary>
    private async Task<bool> LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await db.Workspaces.Where(w => w.Id == workspaceId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.TreeVersion, w => w.TreeVersion + 1), cancellationToken) == 1;

    /// <summary>Locks the page's workspace, then reads the page, so what is read is what the last operation left.</summary>
    private async Task<Page?> LockAndLoadAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var workspaceId = await db.Pages.AsNoTracking()
            .Where(p => p.Id == pageId)
            .Select(p => (Guid?)p.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
        if (workspaceId is null || !await LockWorkspaceAsync(workspaceId.Value, cancellationToken))
        {
            return null;
        }

        return await db.Pages.SingleOrDefaultAsync(p => p.Id == pageId, cancellationToken);
    }

    /// <summary>The page and everything above it, up to the top, each with the time it was trashed, if it was.</summary>
    private async Task<List<ChainLink>> GetChainAsync(Guid pageId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<ChainLink>($"""
            WITH RECURSIVE chain AS (
                SELECT id, parent_id, deleted_at FROM pages WHERE id = {pageId}
              UNION
                SELECT p.id, p.parent_id, p.deleted_at FROM pages p JOIN chain c ON p.id = c.parent_id
            )
            SELECT id AS "Id", deleted_at AS "DeletedAt" FROM chain
            """).ToListAsync(cancellationToken);

    private async Task<bool> IsInTrashAsync(Guid pageId, CancellationToken cancellationToken) =>
        (await GetChainAsync(pageId, cancellationToken)).Any(link => link.DeletedAt is not null);

    /// <summary>The parent must be a page of this workspace that is not in the trash. No parent means the top level.</summary>
    private async Task<TreeError?> CheckParentAsync(Guid workspaceId, Guid? parentId, CancellationToken cancellationToken)
    {
        if (parentId is null)
        {
            return null;
        }

        var parentWorkspace = await db.Pages.AsNoTracking()
            .Where(p => p.Id == parentId)
            .Select(p => (Guid?)p.WorkspaceId)
            .SingleOrDefaultAsync(cancellationToken);
        if (parentWorkspace != workspaceId)
        {
            return new TreeError(StatusCodes.Status400BadRequest, "parent_invalid", "parentId");
        }

        return await IsInTrashAsync(parentId.Value, cancellationToken)
            ? new TreeError(StatusCodes.Status409Conflict, "parent_in_trash", "parentId")
            : null;
    }

    private IQueryable<Page> LiveSiblings(Guid workspaceId, Guid? parentId, Guid? exceptPageId) =>
        db.Pages.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.ParentId == parentId && p.DeletedAt == null && p.Id != exceptPageId);

    private async Task<string?> LastKeyAsync(Guid workspaceId, Guid? parentId, Guid? exceptPageId, CancellationToken cancellationToken) =>
        await LiveSiblings(workspaceId, parentId, exceptPageId).OrderByDescending(p => p.SortKey).Select(p => p.SortKey).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The smallest key above <paramref name="key"/>, or the first key when <paramref name="key"/> is null.</summary>
    private async Task<string?> NextKeyAfterAsync(Guid workspaceId, Guid? parentId, Guid? exceptPageId, string? key, CancellationToken cancellationToken)
    {
        var siblings = LiveSiblings(workspaceId, parentId, exceptPageId);
        if (key is not null)
        {
            siblings = siblings.Where(p => string.Compare(p.SortKey, key) > 0);
        }

        return await siblings.OrderBy(p => p.SortKey).Select(p => p.SortKey).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The key for a page that is to go between two siblings. The neighbours must be live children of the parent, and
    /// next to each other when both are named; with neither named the page goes last.
    /// </summary>
    private async Task<TreeResult<string>> ResolveKeyAsync(Guid workspaceId, Guid? movedPageId, Position position, CancellationToken cancellationToken)
    {
        var (parentId, afterId, beforeId) = position;
        TreeError Invalid() => new(StatusCodes.Status400BadRequest, "position_invalid", "position");

        if (afterId is not null && afterId == beforeId || afterId is not null && afterId == movedPageId || beforeId is not null && beforeId == movedPageId)
        {
            return Invalid();
        }

        string? lower = null;
        string? upper = null;
        if (afterId is not null)
        {
            lower = await LiveSiblings(workspaceId, parentId, movedPageId).Where(p => p.Id == afterId).Select(p => p.SortKey).SingleOrDefaultAsync(cancellationToken);
            if (lower is null)
            {
                return Invalid();
            }

            upper = await NextKeyAfterAsync(workspaceId, parentId, movedPageId, lower, cancellationToken);
            if (beforeId is not null)
            {
                var named = await LiveSiblings(workspaceId, parentId, movedPageId).Where(p => p.Id == beforeId).Select(p => p.SortKey).SingleOrDefaultAsync(cancellationToken);
                if (named is null || named != upper)
                {
                    return Invalid();
                }
            }
        }
        else if (beforeId is not null)
        {
            upper = await LiveSiblings(workspaceId, parentId, movedPageId).Where(p => p.Id == beforeId).Select(p => p.SortKey).SingleOrDefaultAsync(cancellationToken);
            if (upper is null)
            {
                return Invalid();
            }

            lower = await LiveSiblings(workspaceId, parentId, movedPageId)
                .Where(p => string.Compare(p.SortKey, upper) < 0)
                .OrderByDescending(p => p.SortKey)
                .Select(p => p.SortKey)
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            lower = await LastKeyAsync(workspaceId, parentId, movedPageId, cancellationToken);
        }

        return FractionalIndex.KeyBetween(lower, upper);
    }

    /// <summary>Raises the version and the time of change. Called for every change to a page's title or place.</summary>
    private void Touch(Page page)
    {
        page.Version++;
        page.UpdatedAt = time.GetUtcNow();
    }

    /// <summary>
    /// Last write wins, as the architecture says, so a change is never refused for being based on an old version.
    /// But it is logged, and the answer says so, so conflicts can be seen.
    /// </summary>
    private bool NoteConflict(Page page, string operation, int? expectedVersion)
    {
        if (expectedVersion is null || expectedVersion == page.Version)
        {
            return false;
        }

        logger.LogWarning(
            "tree_conflict: {Operation} of page {PageId} was based on version {Expected}, the page is at {Actual}",
            operation, page.Id, expectedVersion, page.Version);
        return true;
    }

    /// <summary>The title trimmed, or null when it is too long. A title may be empty: the client shows "Untitled".</summary>
    private static string? CleanTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        return trimmed.Length > MaxTitleLength ? null : trimmed;
    }

    private static PageDto ToDto(Page page) =>
        new(page.Id, page.ParentId, page.Title, page.SortKey, page.Version, page.UpdatedAt, page.DeletedAt);

    // What a raw query returns; the names match the aliases in the SQL.
    private sealed class PageRow
    {
        public Guid Id { get; set; }

        public Guid? ParentId { get; set; }

        public string Title { get; set; } = "";

        public string SortKey { get; set; } = "";

        public int Version { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }

        public PageDto ToDto() => new(Id, ParentId, Title, SortKey, Version, UpdatedAt, DeletedAt);
    }

    private sealed class ChainLink
    {
        public Guid Id { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }
    }
}
