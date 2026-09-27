using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptionLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "OptionLabels",
                table: "field_definitions",
                type: "text[]",
                nullable: false,
                defaultValue: System.Array.Empty<string>());

            // типы заявок из модели данных кейса (ORDER_1…3) — коды; в уже созданных отделах даём им подписи для экрана
            migrationBuilder.Sql("""
                UPDATE field_definitions
                SET "OptionLabels" = ARRAY['Консультация', 'Оформление', 'Претензия']
                WHERE "Key" IN ('order_type', 'order_types')
                  AND "Options" = ARRAY['ORDER_1', 'ORDER_2', 'ORDER_3']
                  AND cardinality("OptionLabels") = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptionLabels",
                table: "field_definitions");
        }
    }
}
