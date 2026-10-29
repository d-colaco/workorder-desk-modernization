using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WorkOrders.Api.Migrations
{
    /// <inheritdoc />
    public partial class Conflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "confirmed_at",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "conflict_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    legacy_work_order_id = table.Column<int>(type: "integer", nullable: false),
                    field = table.Column<string>(type: "text", nullable: false),
                    legacy_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    winner = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflict_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conflict_log_legacy_work_order_id",
                table: "conflict_log",
                column: "legacy_work_order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conflict_log");

            migrationBuilder.DropColumn(
                name: "confirmed_at",
                table: "outbox");
        }
    }
}
