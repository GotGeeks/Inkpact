using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFileText : Migration
    {
        // As in AddSearch: the characters that join words into one token for Postgres' parser, made spaces before a vector is made.
        private const string Joiners = @".,:;/\@#&%+=?~_-";
        private const string Spaces = "                ";

        private static string Vector(string input, char weight) =>
            $"setweight(to_tsvector('inkpact_search', translate({input}, '{Joiners}', '{Spaces}')), '{weight}')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The text of a note that a page keeps as a file: written by the import, never by the compactor (which rewrites
            // content_text from the document, where the note is only a file node).
            migrationBuilder.AddColumn<string>(
                name: "file_text",
                table: "pages",
                type: "text",
                nullable: false,
                defaultValue: "");

            RemakeSearchVector(migrationBuilder, withFileText: true);

            // The notes an earlier import kept as files have no text here yet. Which file a page shows is in its document, which
            // only the sidecar reads, so their items are marked as converted by an older converter: the next import converts
            // such a note again when nobody changed its page here (the rule of ImportOptions.ConverterVersion), and writes the
            // text of the note as Joplin has it then. A page kept as a file has no text of its own and a Joplin file of
            // the note's (no external id, .md or .html). One that is not a file after all is only converted again, unchanged.
            migrationBuilder.Sql("""
                UPDATE import_items i SET converter_version = 1
                FROM pages p
                WHERE i.kind = 'note' AND i.inkpact_id = p.id AND i.converter_version > 1
                  AND p.file_text = '' AND btrim(p.content_text, E' \t\r\n') = ''
                  AND EXISTS (
                    SELECT 1 FROM attachments a
                    WHERE a.page_id = p.id AND a.external_source_type = 'joplin' AND a.external_id IS NULL
                      AND (a.file_name LIKE '%.md' OR a.file_name LIKE '%.html'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RemakeSearchVector(migrationBuilder, withFileText: false);

            migrationBuilder.DropColumn(
                name: "file_text",
                table: "pages");
        }

        // A generated column's expression cannot be changed: the column and its index are made again. The text part is
        // still at most 200,000 characters, so the vector stays far under the 1 MB a tsvector holds.
        private static void RemakeSearchVector(MigrationBuilder migrationBuilder, bool withFileText)
        {
            var text = withFileText
                ? "left(left(content_text, 200000) || E'\\n' || file_text, 200000)"
                : "left(content_text, 200000)";
            migrationBuilder.Sql("DROP INDEX ix_pages_search_vector;");
            migrationBuilder.Sql("ALTER TABLE pages DROP COLUMN search_vector;");
            migrationBuilder.Sql($"""
                ALTER TABLE pages ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                    {Vector("title", 'A')}
                    || {Vector("tag_text", 'B')}
                    || {Vector(text, 'C')}
                ) STORED;
                """);
            migrationBuilder.Sql("CREATE INDEX ix_pages_search_vector ON pages USING gin (search_vector);");
        }
    }
}
