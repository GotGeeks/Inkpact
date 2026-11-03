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
