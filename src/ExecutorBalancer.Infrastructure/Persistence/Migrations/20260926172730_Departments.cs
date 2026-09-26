using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Departments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_field_definitions_Owner_Key",
                table: "field_definitions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats");

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "weight_rules",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "rules",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "orders",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "field_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "executors",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "executor_hour_stats",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "eligibility_hour_stats",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "audit_entries",
                type: "integer",
                nullable: true);

            // до отделов вся настройка была одна — её история относится к основному отделу; входы остаются общими
            migrationBuilder.Sql("""
                UPDATE audit_entries SET "DepartmentId" = 1 WHERE "Action" NOT IN ('login', 'login_failed');
                """);

            migrationBuilder.AddColumn<int>(
                name: "DepartmentId",
                table: "assignments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats",
                columns: new[] { "DepartmentId", "BucketHour", "SetKey" });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PresetId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.Id);
                });

            // всё, что было до появления отделов, становится «Основным отделом» (Id = 1, Department.DefaultId);
            // счётчик идентификаторов сдвигается, чтобы следующий отдел получил 2
            migrationBuilder.Sql("""
                INSERT INTO departments ("Id", "Code", "Name", "PresetId", "CreatedAt")
                VALUES (1, 'main', 'Основной отдел', NULL, now());
                SELECT setval(pg_get_serial_sequence('departments', 'Id'), 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_weight_rules_DepartmentId",
                table: "weight_rules",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_rules_DepartmentId",
                table: "rules",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_DepartmentId_Status",
                table: "orders",
                columns: new[] { "DepartmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_field_definitions_DepartmentId_Owner_Key",
                table: "field_definitions",
                columns: new[] { "DepartmentId", "Owner", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_executors_DepartmentId",
                table: "executors",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_executor_hour_stats_DepartmentId_BucketHour",
                table: "executor_hour_stats",
                columns: new[] { "DepartmentId", "BucketHour" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_DepartmentId_Id",
                table: "audit_entries",
                columns: new[] { "DepartmentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_department",
                table: "assignments",
                columns: new[] { "DepartmentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_departments_Code",
                table: "departments",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_executors_departments_DepartmentId",
                table: "executors",
                column: "DepartmentId",
                principalTable: "departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_field_definitions_departments_DepartmentId",
                table: "field_definitions",
                column: "DepartmentId",
                principalTable: "departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_orders_departments_DepartmentId",
                table: "orders",
                column: "DepartmentId",
                principalTable: "departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_rules_departments_DepartmentId",
                table: "rules",
                column: "DepartmentId",
                principalTable: "departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_weight_rules_departments_DepartmentId",
                table: "weight_rules",
                column: "DepartmentId",
                principalTable: "departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_executors_departments_DepartmentId",
                table: "executors");

            migrationBuilder.DropForeignKey(
                name: "FK_field_definitions_departments_DepartmentId",
                table: "field_definitions");

            migrationBuilder.DropForeignKey(
                name: "FK_orders_departments_DepartmentId",
                table: "orders");

            migrationBuilder.DropForeignKey(
                name: "FK_rules_departments_DepartmentId",
                table: "rules");

            migrationBuilder.DropForeignKey(
                name: "FK_weight_rules_departments_DepartmentId",
                table: "weight_rules");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropIndex(
                name: "IX_weight_rules_DepartmentId",
                table: "weight_rules");

            migrationBuilder.DropIndex(
                name: "IX_rules_DepartmentId",
                table: "rules");

            migrationBuilder.DropIndex(
                name: "IX_orders_DepartmentId_Status",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_field_definitions_DepartmentId_Owner_Key",
                table: "field_definitions");

            migrationBuilder.DropIndex(
                name: "IX_executors_DepartmentId",
                table: "executors");

            migrationBuilder.DropIndex(
                name: "IX_executor_hour_stats_DepartmentId_BucketHour",
                table: "executor_hour_stats");

            migrationBuilder.DropPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats");

            migrationBuilder.DropIndex(
                name: "IX_audit_entries_DepartmentId_Id",
                table: "audit_entries");

            migrationBuilder.DropIndex(
                name: "ix_assignments_department",
                table: "assignments");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "weight_rules");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "rules");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "field_definitions");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "executors");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "executor_hour_stats");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "eligibility_hour_stats");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "assignments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_eligibility_hour_stats",
                table: "eligibility_hour_stats",
                columns: new[] { "BucketHour", "SetKey" });

            migrationBuilder.CreateIndex(
                name: "IX_field_definitions_Owner_Key",
                table: "field_definitions",
                columns: new[] { "Owner", "Key" },
                unique: true);
        }
    }
}
