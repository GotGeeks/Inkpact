using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTreeVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pages_workspace_id_parent_id_sort_key",
                table: "pages");

            migrationBuilder.AddColumn<long>(
                name: "tree_version",
                table: "workspaces",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "pages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_pages_workspace_id_parent_id_sort_key",
                table: "pages",
                columns: new[] { "workspace_id", "parent_id", "sort_key" },
                unique: true,
                filter: "deleted_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pages_workspace_id_parent_id_sort_key",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "tree_version",
                table: "workspaces");

            migrationBuilder.DropColumn(
                name: "version",
                table: "pages");

            migrationBuilder.CreateIndex(
                name: "ix_pages_workspace_id_parent_id_sort_key",
                table: "pages",
                columns: new[] { "workspace_id", "parent_id", "sort_key" });
        }
    }
}
