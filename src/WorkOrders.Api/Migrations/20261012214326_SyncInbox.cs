using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkOrders.Api.Migrations
{
    /// <inheritdoc />
    public partial class SyncInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "legacy_version",
                table: "work_order",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "processed_message",
                columns: table => new
                {
                    message_id = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_message", x => x.message_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_message");

            migrationBuilder.DropColumn(
                name: "legacy_version",
                table: "work_order");
        }
    }
}
