using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EligibilitySlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats");

            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "eligibility_hour_stats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats",
                columns: new[] { "DepartmentId", "BucketHour", "Slot", "SetKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "eligibility_hour_stats");

            migrationBuilder.AddPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats",
                columns: new[] { "DepartmentId", "BucketHour", "SetKey" });
        }
    }
}
