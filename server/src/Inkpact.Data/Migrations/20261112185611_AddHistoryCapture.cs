using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "content_seq",
                table: "page_versions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "fingerprint",
                table: "page_versions",
                type: "bytea",
                nullable: false,
                defaultValueSql: "'\\x'::bytea");

            migrationBuilder.CreateTable(
                name: "page_history_state",
                columns: table => new
                {
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_seq = table.Column<long>(type: "bigint", nullable: false),
                    captured_updates = table.Column<long>(type: "bigint", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    waiting_attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_history_state", x => x.page_id);
                    table.ForeignKey(
                        name: "fk_page_history_state_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_title_changes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    page_version = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_page_title_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_page_title_changes_changed_by",
                        column: x => x.changed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_page_title_changes_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_page_title_changes_changed_by",
                table: "page_title_changes",
                column: "changed_by");

            migrationBuilder.CreateIndex(
                name: "ix_page_title_changes_page_id_id",
                table: "page_title_changes",
                columns: new[] { "page_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_history_state");

            migrationBuilder.DropTable(
                name: "page_title_changes");

            migrationBuilder.DropColumn(
                name: "content_seq",
                table: "page_versions");

            migrationBuilder.DropColumn(
                name: "fingerprint",
                table: "page_versions");
        }
    }
}
