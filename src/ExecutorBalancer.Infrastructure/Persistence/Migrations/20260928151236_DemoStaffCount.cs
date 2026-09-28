using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DemoStaffCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemoStaffCount",
                table: "departments",
                type: "integer",
                nullable: true);

            // уже заведённый состав считаем исходным: сброс демо вернёт уволенных до этой численности
            migrationBuilder.Sql("""
                UPDATE departments d SET "DemoStaffCount" =
                    (SELECT count(*) FROM executors e WHERE e."DepartmentId" = d."Id")
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DemoStaffCount",
                table: "departments");
        }
    }
}
