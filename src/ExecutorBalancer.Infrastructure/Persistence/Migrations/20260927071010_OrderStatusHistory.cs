using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderStatusHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_status_changes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    DepartmentId = table.Column<int>(type: "integer", nullable: false),
                    From = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    To = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExecutorId = table.Column<long>(type: "bigint", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_status_changes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_OrderId",
                table: "outbox_messages",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_DepartmentId_Id",
                table: "orders",
                columns: new[] { "DepartmentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_status_changes_OrderId",
                table: "order_status_changes",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_status_changes");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_OrderId",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_orders_DepartmentId_Id",
                table: "orders");
        }
    }
}
