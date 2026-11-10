using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "history_cap_bytes",
                table: "workspaces",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "history_daily_days",
                table: "workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "history_enabled",
                table: "workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "history_expiry_days",
                table: "workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.AddColumn<int>(
                name: "history_granular_hours",
                table: "workspaces",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.CreateTable(
                name: "page_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<byte[]>(type: "bytea", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    protected_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payload_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_page_versions_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_page_versions_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_versions_created_at",
                table: "page_versions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_page_versions_created_by",
                table: "page_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_page_versions_page_id_created_at_id",
                table: "page_versions",
                columns: new[] { "page_id", "created_at", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_versions");

            migrationBuilder.DropColumn(
                name: "history_cap_bytes",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "history_daily_days",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "history_enabled",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "history_expiry_days",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "history_granular_hours",
                table: "workspaces");
        }
    }
}
