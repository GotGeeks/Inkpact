using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearch : Migration
    {
        // The characters that join words into one token for Postgres' parser (report.pdf, user@example.com, 3.14,
        // 2026-11-03, a/b): turned into spaces before a vector is made, so the text is split into the runs of letters and
        // digits that a search is split into (SearchWords). Both strings are 16 characters long.
        private const string Joiners = @".,:;/\@#&%+=?~_-";
        private const string Spaces = "                ";

        private static string Vector(string input, char weight) =>
            $"setweight(to_tsvector('inkpact_search', translate({input}, '{Joiners}', '{Spaces}')), '{weight}')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "tag_text",
                table: "pages",
                type: "text",
                nullable: false,
                defaultValue: "");

            // unaccent ships with PostgreSQL and is a trusted extension, so the owner of the database may create it.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");

            // The simple configuration (no stemming, so English and Dutch behave alike) with the accents taken off every word
            // first: unaccent passes its result on to simple, which makes it lower case.
            migrationBuilder.Sql("CREATE TEXT SEARCH CONFIGURATION inkpact_search (COPY = simple);");
            migrationBuilder.Sql("""
                ALTER TEXT SEARCH CONFIGURATION inkpact_search
                    ALTER MAPPING FOR asciiword, word, numword, asciihword, hword, numhword, hword_asciipart, hword_part, hword_numpart
                    WITH unaccent, simple;
                """);

            // Tags are in tables of their own, which a generated column cannot read: their names are kept in tag_text by
            // triggers, so that no way of changing a page's tags or a tag's name can leave the search behind.
            migrationBuilder.Sql("""
                CREATE FUNCTION refresh_tag_text(page_ids uuid[]) RETURNS void LANGUAGE sql AS $$
                    UPDATE pages p SET tag_text = COALESCE((
                        SELECT string_agg(g.name, ' ' ORDER BY g.normalized_name COLLATE "C", g.id)
                        FROM page_tags pt JOIN tags g ON g.id = pt.tag_id
                        WHERE pt.page_id = p.id), '')
                    WHERE p.id = ANY(page_ids);
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION page_tags_changed() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        PERFORM refresh_tag_text(ARRAY[NEW.page_id]);
                    ELSIF TG_OP = 'DELETE' THEN
                        PERFORM refresh_tag_text(ARRAY[OLD.page_id]);
                    ELSE
                        PERFORM refresh_tag_text(ARRAY[OLD.page_id, NEW.page_id]);
                    END IF;
                    RETURN NULL;
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER page_tags_search AFTER INSERT OR UPDATE OR DELETE ON page_tags
                    FOR EACH ROW EXECUTE FUNCTION page_tags_changed();
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION tag_renamed() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM refresh_tag_text(ARRAY(SELECT page_id FROM page_tags WHERE tag_id = NEW.id));
                    RETURN NULL;
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER tags_search AFTER UPDATE OF name ON tags
                    FOR EACH ROW WHEN (OLD.name IS DISTINCT FROM NEW.name) EXECUTE FUNCTION tag_renamed();
                """);

            // The pages of an earlier version get their tag names here.
            migrationBuilder.Sql("SELECT refresh_tag_text(ARRAY(SELECT DISTINCT page_id FROM page_tags));");

            // The pages a person sees: reached from the top without passing the trash. The tree and search both read it, so
            // that search can never find a page the tree hides.
            migrationBuilder.Sql("""
                CREATE FUNCTION live_pages(workspace uuid) RETURNS TABLE (page_id uuid) LANGUAGE sql STABLE AS $$
                    WITH RECURSIVE live AS (
                        SELECT p.id FROM pages p WHERE p.workspace_id = workspace AND p.parent_id IS NULL AND p.deleted_at IS NULL
                      UNION ALL
                        SELECT c.id FROM pages c JOIN live l ON c.parent_id = l.id WHERE c.deleted_at IS NULL
                    )
                    SELECT live.id FROM live
                $$;
                """);

            // search_vector: titles above tags above text. A tsvector holds at most 1 MB, which the text of a large note
            // passes, so only the first 200,000 characters (about 30,000 words) are indexed.
            // title_vector: the title and the tags alone. Ranking has to read a page's vector, which costs what the page is
            // long, and a search of two letters matches nearly every page: the pages that match by their title or tags are
            // found and ranked from this short one instead.
            // Both generated, so that they are right after every kind of write, and added in one statement, which rewrites
            // the table once.
            migrationBuilder.Sql($"""
                ALTER TABLE pages
                    ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                        {Vector("title", 'A')}
                        || {Vector("tag_text", 'B')}
                        || {Vector("left(content_text, 200000)", 'C')}
                    ) STORED,
                    ADD COLUMN title_vector tsvector GENERATED ALWAYS AS (
                        {Vector("title", 'A')}
                        || {Vector("tag_text", 'B')}
                    ) STORED;
                """);
            migrationBuilder.Sql("CREATE INDEX ix_pages_search_vector ON pages USING gin (search_vector);");
            migrationBuilder.Sql("CREATE INDEX ix_pages_title_vector ON pages USING gin (title_vector);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_pages_title_vector;");
            migrationBuilder.Sql("DROP INDEX ix_pages_search_vector;");
            migrationBuilder.Sql("ALTER TABLE pages DROP COLUMN title_vector, DROP COLUMN search_vector;");
            migrationBuilder.Sql("DROP FUNCTION live_pages(uuid);");
            migrationBuilder.Sql("DROP TRIGGER tags_search ON tags;");
            migrationBuilder.Sql("DROP TRIGGER page_tags_search ON page_tags;");
            migrationBuilder.Sql("DROP FUNCTION tag_renamed();");
            migrationBuilder.Sql("DROP FUNCTION page_tags_changed();");
            migrationBuilder.Sql("DROP FUNCTION refresh_tag_text(uuid[]);");
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION inkpact_search;");

            migrationBuilder.DropColumn(
                name: "tag_text",
                table: "pages");
        }
    }
}
