using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Analytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eligibility_hour_stats",
                columns: table => new
                {
                    BucketHour = table.Column<long>(type: "bigint", nullable: false),
                    SetKey = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eligibility_hour_stats", x => new { x.BucketHour, x.SetKey });
                });

            migrationBuilder.CreateTable(
                name: "executor_hour_stats",
                columns: table => new
                {
                    BucketHour = table.Column<long>(type: "bigint", nullable: false),
                    ExecutorId = table.Column<long>(type: "bigint", nullable: false),
                    AssignedCount = table.Column<int>(type: "integer", nullable: false),
                    AssignedWeight = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    PrimaryCount = table.Column<int>(type: "integer", nullable: false),
                    ReassignCount = table.Column<int>(type: "integer", nullable: false),
                    ParentCount = table.Column<int>(type: "integer", nullable: false),
                    SecondaryCount = table.Column<int>(type: "integer", nullable: false),
                    FreeWeight = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    ClosedCount = table.Column<int>(type: "integer", nullable: false),
                    ReturnedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_executor_hour_stats", x => new { x.BucketHour, x.ExecutorId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "eligibility_hour_stats");

            migrationBuilder.DropTable(
                name: "executor_hour_stats");
        }
    }
}
