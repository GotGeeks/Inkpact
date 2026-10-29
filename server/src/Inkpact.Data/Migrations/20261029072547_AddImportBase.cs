using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "base",
                table: "import_items",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "converter_version",
                table: "import_items",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<byte[]>(
                name: "page_fingerprint",
                table: "import_items",
                type: "bytea",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "base",
                table: "import_items");

            migrationBuilder.DropColumn(
                name: "converter_version",
                table: "import_items");

            migrationBuilder.DropColumn(
                name: "page_fingerprint",
                table: "import_items");
        }
    }
}
