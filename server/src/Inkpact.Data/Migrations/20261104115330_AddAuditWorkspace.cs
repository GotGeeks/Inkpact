using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inkpact.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "workspace_id",
                table: "audit_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_workspace_id",
                table: "audit_events",
                column: "workspace_id");

            migrationBuilder.AddForeignKey(
                name: "fk_audit_events_workspace_id",
                table: "audit_events",
                column: "workspace_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_audit_events_workspace_id",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_workspace_id",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                table: "audit_events");
        }
    }
}
