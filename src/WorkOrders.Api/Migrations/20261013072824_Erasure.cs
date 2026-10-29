using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkOrders.Api.Migrations
{
    /// <inheritdoc />
    public partial class Erasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "subject_business_entity_id",
                table: "outbox",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "erasure_log",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_business_entity_id = table.Column<int>(type: "integer", nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    legacy_confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    new_purged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_log", x => x.request_id);
                });

            migrationBuilder.CreateTable(
                name: "planner",
                columns: table => new
                {
                    business_entity_id = table.Column<int>(type: "integer", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    erased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_planner", x => x.business_entity_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erasure_log");

            migrationBuilder.DropTable(
                name: "planner");

            migrationBuilder.DropColumn(
                name: "subject_business_entity_id",
                table: "outbox");
        }
    }
}
