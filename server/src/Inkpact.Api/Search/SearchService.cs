using Inkpact.Data;
using Microsoft.EntityFrameworkCore;

namespace Inkpact.Api.Search;

/// <summary>One page that matched: its title and a piece of its text, each with the matches marked. An empty title is a page with none.</summary>
public sealed record SearchHit(Guid Id, IReadOnlyList<SearchSegment> Title, IReadOnlyList<SearchSegment> Snippet);

/// <summary>The pages found, and whether more pages matched than are in the list (a longer search narrows it).</summary>
public sealed record SearchResponse(IReadOnlyList<SearchHit> Results, bool More = false);

/// <summary>
/// Full-text search in one workspace (run 1.17). Raw SQL, as the tree's recursive queries are: the pages that count are the
/// ones <c>live_pages</c> gives, the same function the tree reads, so a page in the trash, or under one, is never found.
/// </summary>
public sealed class SearchService(InkpactDbContext db)
{
    public const int MaxResults = 30;

    /// <summary>How many of the pages that match in their text only are ranked, newest edit first. See <see cref="SearchAsync"/>.</summary>
    public const int TextCandidates = 1_000;

    /// <summary>What is indexed of a page's text (migration AddSearch); a match further on cannot be found.</summary>
    public const int IndexedText = 200_000;

    // Where the first matching word is looked for first, before the whole indexed text.
    private const int FirstLook = 4_000;

    // The snippet: the text from this far before the first matching word, this long.
    private const int SnippetLead = 150;
    private const int SnippetLength = 400;

    /// <summary>
    /// The pages whose title or tags match come first, ranked on their short vector (a title above a tag, then the newest
    /// edit); then the pages that match in their text only. Reading a page's whole vector costs what the page is long, so
    /// those are ranked among the <see cref="TextCandidates"/> that were edited last; a search with more matches than that
    /// (two letters in a large workspace) answers <c>More</c>, as it does whenever the list is not all there is.
    /// </summary>
    public async Task<SearchResponse> SearchAsync(Guid workspaceId, string? text, CancellationToken cancellationToken = default)
    {
        var words = SearchWords.Split(text);
        if (words.Count == 0)
        {
            return new SearchResponse([]);
        }

        var query = SearchWords.ToTsQuery(words);
        // Any of the words in the text itself (weight C), not only in the title or the tags: then the text has a snippet.
        var inText = string.Join(" | ", words.Select(w => w + ":*C"));
        var pattern = SearchHeadline.Pattern(words);

        // by_text leaves out what by_title found; when by_title is full (30), the list is by_title alone, so a title match
        // always comes before a text match. The snippet is the text around the first matching word, which the regular
        // expression finds as the index would (case and accents left out) and stops at.
        var rows = await db.Database.SqlQuery<HitRow>($"""
            WITH live AS MATERIALIZED (SELECT page_id AS id FROM live_pages({workspaceId})),
            q AS (
                SELECT to_tsquery('inkpact_search', {query}) AS query,
                       to_tsquery('inkpact_search', {inText}) AS in_text
            ),
            matches AS MATERIALIZED (
                SELECT p.id, p.updated_at
                FROM pages p JOIN live ON live.id = p.id CROSS JOIN q
                WHERE p.search_vector @@ q.query
            ),
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
                    SELECT m.id FROM matches m
                    WHERE m.id NOT IN (SELECT id FROM by_title)
                    ORDER BY m.updated_at DESC, m.id
                    LIMIT {TextCandidates}
                ) c JOIN pages p ON p.id = c.id CROSS JOIN q
            ),
            hits AS (
                SELECT * FROM (SELECT * FROM by_title UNION ALL SELECT * FROM by_text) u
                ORDER BY part, rank DESC, updated_at DESC, id
                LIMIT {MaxResults}
            )
            SELECT h.id AS "Id", p.title AS "Title", e.snippet AS "Snippet",
                   m.at > 0 AND m.at <= {SnippetLead} + 1 AS "SnippetAtStart",
                   COALESCE(char_length(e.snippet) < {SnippetLength}, false) AS "SnippetAtEnd",
                   p.file_text <> '' AS "HasFileText",
                   (SELECT count(*) FROM matches)::int AS "Total"
            FROM hits h JOIN pages p ON p.id = h.id CROSS JOIN q
            CROSS JOIN LATERAL (
                -- The expression reads the text it is given to the end, so the start of the text is looked at first.
                SELECT CASE WHEN p.search_vector @@ q.in_text
                            THEN COALESCE(NULLIF(regexp_instr(left(p.content_text, {FirstLook}), {pattern}), 0),
                                          regexp_instr(left(p.content_text, {IndexedText}), {pattern}))
                            ELSE 0 END AS at
            ) m
            CROSS JOIN LATERAL (
                SELECT CASE WHEN m.at > 0 THEN substr(p.content_text, greatest(m.at - {SnippetLead}, 1), {SnippetLength}) END AS snippet
            ) e
            ORDER BY h.part, h.rank DESC, h.updated_at DESC, h.id
            """).ToListAsync(cancellationToken);

        await AddSnippetsFromFilesAsync(rows, pattern, cancellationToken);

        return new SearchResponse(
            [
                .. rows.Select(r => new SearchHit(
                    r.Id,
                    SearchHeadline.Mark(r.Title, words, atStart: true, atEnd: true),
                    SearchHeadline.OnlyWithMatch(SearchHeadline.Mark(r.Snippet, words, r.SnippetAtStart, r.SnippetAtEnd)))),
            ],
            rows.Count > 0 && rows[0].Total > rows.Count);
    }

    /// <summary>
    /// A page that keeps a note as a file has the note's words in <c>file_text</c> (migration AddFileText), after its content in the text
    /// the vector is made from. The snippet of such a page comes from there when the content has no match. A query of its own, for
    /// those pages only: reading a second text for every result would cost every search, and few pages are files.
    /// </summary>
    private async Task AddSnippetsFromFilesAsync(List<HitRow> rows, string pattern, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.Snippet is null && r.HasFileText).Select(r => r.Id).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var found = await db.Database.SqlQuery<HitRow>($"""
            SELECT p.id AS "Id", '' AS "Title", e.snippet AS "Snippet",
                   m.at <= {SnippetLead} + 1 AS "SnippetAtStart",
                   char_length(e.snippet) < {SnippetLength} AS "SnippetAtEnd",
                   true AS "HasFileText", 0 AS "Total"
            FROM pages p
            CROSS JOIN LATERAL (
                SELECT left(left(p.content_text, {IndexedText}) || E'\n' || p.file_text, {IndexedText}) AS text
            ) t
            CROSS JOIN LATERAL (SELECT regexp_instr(t.text, {pattern}) AS at) m
            CROSS JOIN LATERAL (SELECT substr(t.text, greatest(m.at - {SnippetLead}, 1), {SnippetLength}) AS snippet) e
            WHERE p.id = ANY({ids}) AND m.at > 0
            """).ToListAsync(cancellationToken);

        foreach (var snippet in found)
        {
            var row = rows.First(r => r.Id == snippet.Id);
            (row.Snippet, row.SnippetAtStart, row.SnippetAtEnd) = (snippet.Snippet, snippet.SnippetAtStart, snippet.SnippetAtEnd);
        }
    }

    // What the raw query returns; the names match the aliases in the SQL.
    private sealed class HitRow
    {
        public Guid Id { get; set; }

        public string Title { get; set; } = "";

        public string? Snippet { get; set; }

        public bool SnippetAtStart { get; set; }

        public bool SnippetAtEnd { get; set; }

        public bool HasFileText { get; set; }

        public int Total { get; set; }
    }
}
