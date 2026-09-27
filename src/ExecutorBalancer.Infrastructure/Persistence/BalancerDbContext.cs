using ExecutorBalancer.Application;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExecutorBalancer.Infrastructure.Persistence;

public sealed class BalancerDbContext(DbContextOptions<BalancerDbContext> options)
    : DbContext(options), IBalancerDbContext
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Executor> Executors => Set<Executor>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<WeightRule> WeightRules => Set<WeightRule>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OrderStatusChange> OrderStatusChanges => Set<OrderStatusChange>();
    public DbSet<ExecutorHourStat> ExecutorHourStats => Set<ExecutorHourStat>();
    public DbSet<EligibilityHourStat> EligibilityHourStats => Set<EligibilityHourStat>();

    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public void Detach(object entity) => Entry(entity).State = EntityState.Detached;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var model = modelBuilder;
        model.Entity<Department>(e =>
        {
            e.ToTable("departments");
            e.Property(x => x.Code).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.PresetId).HasMaxLength(32);
            e.Property(x => x.SphereTitle).HasMaxLength(120);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.ReworkPenalty).HasPrecision(4, 3);
            e.Property(x => x.FastClosePenalty).HasPrecision(4, 3);
            e.Property(x => x.QualityThreshold).HasPrecision(4, 3);
            e.Property(x => x.HeavyQualityThreshold).HasPrecision(4, 3);
            e.Property(x => x.HeavyWeight).HasPrecision(10, 3);
        });

        model.Entity<Executor>(e =>
        {
            e.ToTable("executors");
            e.HasIndex(x => x.DepartmentId);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FullName).HasMaxLength(300);
            e.Property(x => x.QualificationWeight).HasPrecision(10, 3);
            e.Property(x => x.AttributesJson).HasColumnType("jsonb");
        });

        model.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Weight).HasPrecision(10, 3);
            e.Property(x => x.Points).HasPrecision(10, 3);
            e.Property(x => x.AttributesJson).HasColumnType("jsonb");
            e.Property(x => x.PendingReason).HasMaxLength(300);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => new { x.Status, x.ExecutorId });
            e.HasIndex(x => new { x.DepartmentId, x.Status });
            e.HasIndex(x => new { x.DepartmentId, x.Id }); // список заявок отдела, новые сверху
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Assignment>(e =>
        {
            e.ToTable("assignments");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OrderWeight).HasPrecision(10, 3);
            e.Property(x => x.Score).HasPrecision(18, 6);
            e.Property(x => x.ExplanationJson).HasColumnType("jsonb");
            // вторая линия защиты от двойного назначения: текущее решение по заявке только одно
            e.HasIndex(x => x.OrderId, "ux_assignments_current_order").IsUnique().HasFilter("\"IsCurrent\"");
            e.HasIndex(x => x.OrderId, "ix_assignments_order");
            e.HasIndex(x => x.CreatedAt, "ix_assignments_created_at");
            e.HasIndex(x => new { x.DepartmentId, x.Id }, "ix_assignments_department");
        });

        model.Entity<FieldDefinition>(e =>
        {
            e.ToTable("field_definitions");
            e.Property(x => x.Owner).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Key).HasMaxLength(48);
            e.Property(x => x.Label).HasMaxLength(120);
            e.HasIndex(x => new { x.DepartmentId, x.Owner, x.Key }).IsUnique();
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Rule>(e =>
        {
            e.ToTable("rules");
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.OrderField).HasMaxLength(48);
            e.Property(x => x.Operator).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Target).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ExecutorField).HasMaxLength(48);
            e.Property(x => x.ExecutorFieldUpper).HasMaxLength(48);
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
            e.HasIndex(x => x.DepartmentId);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WeightRule>(e =>
        {
            e.ToTable("weight_rules");
            e.Property(x => x.OrderField).HasMaxLength(48);
            e.Property(x => x.Operator).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
            e.Property(x => x.Weight).HasPrecision(10, 3);
            e.HasIndex(x => x.DepartmentId);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
            e.HasIndex(x => x.OrderId); // доставка в АИС в карточке заявки
        });

        model.Entity<OrderStatusChange>(e =>
        {
            e.ToTable("order_status_changes");
            e.Property(x => x.From).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.To).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.OrderId);
        });

        model.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.Property(x => x.Actor).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(50);
            e.Property(x => x.Entity).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(50);
            e.Property(x => x.DataJson).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.DepartmentId, x.Id });
        });

        model.Entity<ExecutorHourStat>(e =>
        {
            e.ToTable("executor_hour_stats");
            e.HasKey(x => new { x.BucketHour, x.ExecutorId });
            e.HasIndex(x => new { x.DepartmentId, x.BucketHour });
            e.Property(x => x.AssignedWeight).HasPrecision(18, 3);
            e.Property(x => x.FreeWeight).HasPrecision(18, 3);
            e.Property(x => x.ClosedWeight).HasPrecision(18, 3);
            e.Property(x => x.Points).HasPrecision(18, 3);
        });

        model.Entity<EligibilityHourStat>(e =>
        {
            e.ToTable("eligibility_hour_stats");
            e.HasKey(x => new { x.DepartmentId, x.BucketHour, x.SetKey });
            e.Property(x => x.SetKey).HasMaxLength(EligibilityHourStat.MaxSetKeyLength);
            e.Property(x => x.Weight).HasPrecision(18, 3);
        });
    }
}
