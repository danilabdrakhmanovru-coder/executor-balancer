using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Executors;

/// <summary>Удаление рабочих данных отдела — для сброса демо и песочниц гостей. Настройки и сотрудники не трогаются.</summary>
public static class DepartmentData
{
    /// <summary>
    /// Удаляет заявки отдела, назначения, историю статусов, outbox и почасовую статистику одной транзакцией.
    /// Возвращает номера удалённых заявок — их надо забыть и в хранилище нагрузки.
    /// </summary>
    public static async Task<IReadOnlyList<long>> WipeOrdersAsync(IBalancerDbContext db, int departmentId,
        CancellationToken cancellationToken)
    {
        var orderIds = await db.Orders.AsNoTracking().Where(o => o.DepartmentId == departmentId).Select(o => o.Id)
            .ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var orders = db.Orders.Where(o => o.DepartmentId == departmentId).Select(o => o.Id);
        await db.OutboxMessages.Where(m => orders.Contains(m.OrderId)).ExecuteDeleteAsync(cancellationToken);
        await db.OrderStatusChanges.Where(c => c.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        await db.Assignments.Where(a => a.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        await db.ExecutorHourStats.Where(x => x.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        await db.EligibilityHourStats.Where(x => x.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        await db.Orders.Where(o => o.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return orderIds;
    }
}
