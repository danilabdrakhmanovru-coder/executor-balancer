using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ExecutorBalancer.Application;

public interface IBalancerDbContext
{
    DbSet<Department> Departments { get; }
    DbSet<Order> Orders { get; }
    DbSet<Executor> Executors { get; }
    DbSet<Assignment> Assignments { get; }
    DbSet<FieldDefinition> FieldDefinitions { get; }
    DbSet<Rule> Rules { get; }
    DbSet<WeightRule> WeightRules { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<OrderStatusChange> OrderStatusChanges { get; }
    DbSet<ExecutorHourStat> ExecutorHourStats { get; }
    DbSet<EligibilityHourStat> EligibilityHourStats { get; }
    DbSet<User> Users { get; }
    DbSet<ExecutorQualification> ExecutorQualifications { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Нарушение уникального ключа — например, ту же заявку одновременно прислали дважды.</summary>
    bool IsUniqueViolation(DbUpdateException exception);

    void Detach(object entity);
}
