using ExecutorBalancer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ExecutorBalancer.Infrastructure.Persistence.Migrations;

[DbContext(typeof(BalancerDbContext))]
[Migration("20260926090000_Initial")]
public partial class Initial : Migration
{
    private const string Identity = "Npgsql:ValueGenerationStrategy";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "executors",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false),
                FullName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                DailyLimit = table.Column<int>(type: "integer", nullable: true),
                QualificationWeight = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                AttributesJson = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_executors", x => x.Id));

        migrationBuilder.CreateTable(
            name: "orders",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false),
                ParentId = table.Column<long>(type: "bigint", nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Weight = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                AttributesJson = table.Column<string>(type: "jsonb", nullable: false),
                ExecutorId = table.Column<long>(type: "bigint", nullable: true),
                PendingReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_orders", x => x.Id));

        migrationBuilder.CreateTable(
            name: "assignments",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation(Identity, NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OrderId = table.Column<long>(type: "bigint", nullable: false),
                ExecutorId = table.Column<long>(type: "bigint", nullable: false),
                Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                OrderWeight = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                Score = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                ExplanationJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_assignments", x => x.Id));

        migrationBuilder.CreateTable(
            name: "field_definitions",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation(Identity, NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Owner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Key = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Options = table.Column<string[]>(type: "text[]", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_field_definitions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "rules",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation(Identity, NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                OrderField = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                Operator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ExecutorField = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                ExecutorFieldUpper = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                ValueJson = table.Column<string>(type: "jsonb", nullable: true),
                IsStrict = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_rules", x => x.Id));

        migrationBuilder.CreateTable(
            name: "weight_rules",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation(Identity, NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                OrderField = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                Operator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ValueJson = table.Column<string>(type: "jsonb", nullable: false),
                Weight = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_weight_rules", x => x.Id));

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation(Identity, NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OrderId = table.Column<long>(type: "bigint", nullable: false),
                ExecutorId = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_outbox_messages", x => x.Id));

        migrationBuilder.CreateTable(
            name: "audit_entries",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation(Identity, NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Entity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                EntityId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                DataJson = table.Column<string>(type: "jsonb", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_audit_entries", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_orders_ParentId", table: "orders", column: "ParentId");
        migrationBuilder.CreateIndex(name: "IX_orders_Status_ExecutorId", table: "orders",
            columns: ["Status", "ExecutorId"]);
        migrationBuilder.CreateIndex(name: "ux_assignments_current_order", table: "assignments", column: "OrderId",
            unique: true, filter: "\"IsCurrent\"");
        migrationBuilder.CreateIndex(name: "ix_assignments_order", table: "assignments", column: "OrderId");
        migrationBuilder.CreateIndex(name: "ix_assignments_created_at", table: "assignments", column: "CreatedAt");
        migrationBuilder.CreateIndex(name: "IX_field_definitions_Owner_Key", table: "field_definitions",
            columns: ["Owner", "Key"], unique: true);
        migrationBuilder.CreateIndex(name: "IX_outbox_messages_SentAt_NextAttemptAt", table: "outbox_messages",
            columns: ["SentAt", "NextAttemptAt"]);
        migrationBuilder.CreateIndex(name: "IX_audit_entries_CreatedAt", table: "audit_entries", column: "CreatedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "audit_entries", "outbox_messages", "weight_rules", "rules",
                     "field_definitions", "assignments", "orders", "executors" })
        {
            migrationBuilder.DropTable(name: table);
        }
    }
}
