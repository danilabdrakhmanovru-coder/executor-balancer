using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SphereTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SphereTitle",
                table: "departments",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SphereTitle",
                table: "departments");
        }
    }
}
