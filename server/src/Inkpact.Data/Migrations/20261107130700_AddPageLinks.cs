using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "links_indexed",
                table: "pages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "page_links",
                columns: table => new
                {
                    source_page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_page_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_links", x => new { x.source_page_id, x.target_page_id });
                    table.ForeignKey(
                        name: "fk_page_links_source_page_id",
                        column: x => x.source_page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_links_target_page_id",
                table: "page_links",
                column: "target_page_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_links");

            migrationBuilder.DropColumn(
                name: "links_indexed",
                table: "pages");
        }
    }
}
