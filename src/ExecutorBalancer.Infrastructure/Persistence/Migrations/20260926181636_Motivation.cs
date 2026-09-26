using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Motivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Points",
                table: "orders",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReworkCount",
                table: "orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ExtraPercent",
                table: "executors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ClosedWeight",
                table: "executor_hour_stats",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ExtraCount",
                table: "executor_hour_stats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FastClosedCount",
                table: "executor_hour_stats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "Points",
                table: "executor_hour_stats",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "FastClosePenalty",
                table: "departments",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: false,
                defaultValue: 0.5m);

            migrationBuilder.AddColumn<int>(
                name: "FastCloseSeconds",
                table: "departments",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<decimal>(
                name: "HeavyQualityThreshold",
                table: "departments",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: false,
                defaultValue: 0.9m);

            migrationBuilder.AddColumn<decimal>(
                name: "HeavyWeight",
                table: "departments",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: false,
                defaultValue: 3m);

            migrationBuilder.AddColumn<int>(
                name: "MaxExtraPercent",
                table: "departments",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<decimal>(
                name: "QualityThreshold",
                table: "departments",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: false,
                defaultValue: 0.8m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReworkPenalty",
                table: "departments",
                type: "numeric(4,3)",
                precision: 4,
                scale: 3,
                nullable: false,
                defaultValue: 0.25m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Points",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ReworkCount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ExtraPercent",
                table: "executors");

            migrationBuilder.DropColumn(
                name: "ClosedWeight",
                table: "executor_hour_stats");

            migrationBuilder.DropColumn(
                name: "ExtraCount",
                table: "executor_hour_stats");

            migrationBuilder.DropColumn(
                name: "FastClosedCount",
                table: "executor_hour_stats");

            migrationBuilder.DropColumn(
                name: "Points",
                table: "executor_hour_stats");

            migrationBuilder.DropColumn(
                name: "FastClosePenalty",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "FastCloseSeconds",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "HeavyQualityThreshold",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "HeavyWeight",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "MaxExtraPercent",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "QualityThreshold",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "ReworkPenalty",
                table: "departments");
        }
    }
}
