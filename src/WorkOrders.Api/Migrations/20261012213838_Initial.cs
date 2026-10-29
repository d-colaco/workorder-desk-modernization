using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkOrders.Api.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_order",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legacy_work_order_id = table.Column<int>(type: "integer", nullable: true),
                    legacy_product_id = table.Column<int>(type: "integer", nullable: false),
                    product_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    order_qty = table.Column<int>(type: "integer", nullable: false),
                    scrapped_qty = table.Column<int>(type: "integer", nullable: false),
                    start_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    end_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    due_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    scrap_reason_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_order", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "work_order_operation",
                columns: table => new
                {
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_sequence = table.Column<short>(type: "smallint", nullable: false),
                    location_id = table.Column<short>(type: "smallint", nullable: false),
                    location_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    scheduled_start_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    scheduled_end_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    actual_start_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    actual_end_date = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    actual_resource_hrs = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    planned_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    actual_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_order_operation", x => new { x.work_order_id, x.operation_sequence });
                    table.ForeignKey(
                        name: "fk_work_order_operation_work_order_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_legacy_work_order_id",
                table: "work_order",
                column: "legacy_work_order_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_operation");

            migrationBuilder.DropTable(
                name: "work_order");
        }
    }
}
