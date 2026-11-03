using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Search;

/// <summary>One page that matched: its title and a piece of its text, each with the matches marked. An empty title is a page with none.</summary>
public sealed record SearchHit(Guid Id, IReadOnlyList<SearchSegment> Title, IReadOnlyList<SearchSegment> Snippet);

public sealed record SearchResponse(IReadOnlyList<SearchHit> Results);

/// <summary>
/// Full-text search in one workspace (run 1.17). Raw SQL, as the tree's recursive queries are: the pages that count are the
/// ones reached from the top without passing the trash, which a page in the trash, or under one, is not.
/// </summary>
public sealed class SearchService(InkpactDbContext db)
{
    public const int MaxResults = 30;

    /// <summary>How many of the pages that match in their text only are ranked, newest edit first. See <see cref="SearchAsync"/>.</summary>
    public const int TextCandidates = 150;

    /// <summary>The snippet is made from the start of the text this long: the headline parses what it is given, which costs what that is long.</summary>
    public const int SnippetWindow = 20_000;

    /// <summary>
    /// The pages whose title or tags match come first, ranked on their short vector (a title above a tag, then the newest
    /// edit); then the pages that match in their text only. Reading a page's whole vector costs what the page is long, and
    /// two typed letters match nearly every page, so those are ranked only among the <see cref="TextCandidates"/> that were
    /// edited last: the more letters, the fewer pages match and the more exactly they are ranked.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(Guid workspaceId, string? text, CancellationToken cancellationToken = default)
    {
        var query = SearchWords.ToTsQuery(text);
        if (query is null)
        {
            return new SearchResponse([]);
        }

        // Only the 30 are marked up, as the headline parses the text again. The marks are control characters, taken out of
        // the text first so that none of its own can pass for one.
        var rows = await db.Database.SqlQuery<HitRow>($"""
            WITH RECURSIVE live AS (
                SELECT id FROM pages WHERE workspace_id = {workspaceId} AND parent_id IS NULL AND deleted_at IS NULL
              UNION ALL
                SELECT c.id FROM pages c JOIN live p ON c.parent_id = p.id WHERE c.deleted_at IS NULL
            ),
            q AS (SELECT to_tsquery('inkpact_search', {query}) AS query),
            by_title AS (
                SELECT p.id, p.updated_at, ts_rank(p.title_vector, q.query) AS rank, 1 AS part
                FROM pages p JOIN live ON live.id = p.id CROSS JOIN q
                WHERE p.title_vector @@ q.query
                ORDER BY rank DESC, p.updated_at DESC, p.id
                LIMIT {MaxResults}
            ),
            by_text AS (
                SELECT p.id, p.updated_at, ts_rank(p.search_vector, q.query) AS rank, 2 AS part
                FROM (
                    SELECT p.id, p.updated_at
                    FROM pages p JOIN live ON live.id = p.id CROSS JOIN q
                    WHERE p.search_vector @@ q.query AND p.id NOT IN (SELECT id FROM by_title)
                    ORDER BY p.updated_at DESC, p.id
                    LIMIT {TextCandidates}
                ) p0 JOIN pages p ON p.id = p0.id CROSS JOIN q
            ),
            hits AS (
                SELECT * FROM (SELECT * FROM by_title UNION ALL SELECT * FROM by_text) u
                ORDER BY part, rank DESC, updated_at DESC, id
                LIMIT {MaxResults}
            )
            SELECT h.id AS "Id",
                   ts_headline('inkpact_search', translate(p.title, E'\x01\x02\x03', ''), q.query,
                               E'HighlightAll=true, StartSel=\x01, StopSel=\x02') AS "Title",
                   ts_headline('inkpact_search', translate(left(p.content_text, {SnippetWindow}), E'\x01\x02\x03', ''), q.query,
                               E'MaxFragments=2, MaxWords=20, MinWords=8, StartSel=\x01, StopSel=\x02, FragmentDelimiter=\x03') AS "Snippet"
            FROM hits h JOIN pages p ON p.id = h.id CROSS JOIN q
            ORDER BY h.part, h.rank DESC, h.updated_at DESC, h.id
            """).ToListAsync(cancellationToken);

        return new SearchResponse(
        [
            .. rows.Select(r => new SearchHit(
                r.Id,
                SearchHeadline.Segments(r.Title),
                SearchHeadline.OnlyWithMatch(SearchHeadline.Segments(r.Snippet)))),
        ]);
    }

    // What the raw query returns; the names match the aliases in the SQL.
    private sealed class HitRow
    {
        public Guid Id { get; set; }

        public string? Title { get; set; }

        public string? Snippet { get; set; }
    }
}
