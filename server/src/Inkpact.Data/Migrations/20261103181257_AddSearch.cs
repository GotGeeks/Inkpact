using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearch : Migration
    {
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

            // Tags are in tables of their own, which a generated column cannot read: the names go in a column that the code
            // that changes a page's tags keeps (PageSearchText). The pages of an earlier version get it here.
            migrationBuilder.Sql("""
                UPDATE pages p SET tag_text = t.names
                FROM (
                    SELECT pt.page_id, string_agg(g.name, ' ' ORDER BY g.normalized_name COLLATE "C", g.id) AS names
                    FROM page_tags pt JOIN tags g ON g.id = pt.tag_id
                    GROUP BY pt.page_id
                ) t
                WHERE t.page_id = p.id;
                """);

            // Titles above tags above text. A tsvector holds at most 1 MB, which the text of a large note passes, so only the
            // first 200,000 characters (about 30,000 words) are indexed. Generated, so that it is right after every kind of
            // write: a rename, a compaction, an import.
            migrationBuilder.Sql("""
                ALTER TABLE pages ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('inkpact_search', title), 'A')
                    || setweight(to_tsvector('inkpact_search', tag_text), 'B')
                    || setweight(to_tsvector('inkpact_search', left(content_text, 200000)), 'C')
                ) STORED;
                """);
            migrationBuilder.Sql("CREATE INDEX ix_pages_search_vector ON pages USING gin (search_vector);");

            // The same for the title and the tags alone. A page's whole vector has to be read to rank it, which costs what the
            // page is long, and a search of two letters matches nearly every page: the pages that match by their title or
            // their tags are found and ranked from this short one instead.
            migrationBuilder.Sql("""
                ALTER TABLE pages ADD COLUMN title_vector tsvector GENERATED ALWAYS AS (
                    setweight(to_tsvector('inkpact_search', title), 'A')
                    || setweight(to_tsvector('inkpact_search', tag_text), 'B')
                ) STORED;
                """);
            migrationBuilder.Sql("CREATE INDEX ix_pages_title_vector ON pages USING gin (title_vector);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_pages_title_vector;");
            migrationBuilder.Sql("ALTER TABLE pages DROP COLUMN title_vector;");
            migrationBuilder.Sql("DROP INDEX ix_pages_search_vector;");
            migrationBuilder.Sql("ALTER TABLE pages DROP COLUMN search_vector;");
            migrationBuilder.Sql("DROP TEXT SEARCH CONFIGURATION inkpact_search;");

            migrationBuilder.DropColumn(
                name: "tag_text",
                table: "pages");
        }
    }
}
