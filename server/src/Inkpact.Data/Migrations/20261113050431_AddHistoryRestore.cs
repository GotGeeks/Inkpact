using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "page_restores",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    before_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    restore_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    page_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_restores", x => x.operation_id);
                    table.ForeignKey(
                        name: "fk_page_restores_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_page_restores_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_restores_page_id",
                table: "page_restores",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "ix_page_restores_user_id",
                table: "page_restores",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_restores");
        }
    }
}
