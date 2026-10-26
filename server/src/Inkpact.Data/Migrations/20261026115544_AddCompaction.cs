using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_text",
                table: "pages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "page_snapshots",
                columns: table => new
                {
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<byte[]>(type: "bytea", nullable: false),
                    state_vector = table.Column<byte[]>(type: "bytea", nullable: false),
                    watermark = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    update_count = table.Column<long>(type: "bigint", nullable: false),
                    history_complete = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_snapshots", x => x.page_id);
                    table.ForeignKey(
                        name: "fk_page_snapshots_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_updates_archive",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_updates_archive", x => x.seq);
                    table.ForeignKey(
                        name: "fk_page_updates_archive_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_page_updates_archive_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_updates_archive_archived_at",
                table: "page_updates_archive",
                column: "archived_at");

            migrationBuilder.CreateIndex(
                name: "ix_page_updates_archive_page_id_seq",
                table: "page_updates_archive",
                columns: new[] { "page_id", "seq" });

            migrationBuilder.CreateIndex(
                name: "ix_page_updates_archive_user_id",
                table: "page_updates_archive",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_snapshots");

            migrationBuilder.DropTable(
                name: "page_updates_archive");

            migrationBuilder.DropColumn(
                name: "content_text",
                table: "pages");
        }
    }
}
