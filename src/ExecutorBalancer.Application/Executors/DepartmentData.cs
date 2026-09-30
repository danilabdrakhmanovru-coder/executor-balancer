using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Executors;

/// <summary>Удаление рабочих данных отдела — для сброса демо и песочниц гостей. Настройки и сотрудники не трогаются.</summary>
public static class DepartmentData
{
    /// <summary>Сколько заявок удаляется за один шаг: каждый шаг — короткая транзакция, укладывается в таймаут команды.</summary>
    public const int WipeBatch = 2000;

    /// <summary>
    /// Удаляет заявки отдела, назначения, историю статусов, outbox и почасовую статистику. Порциями по
    /// <see cref="WipeBatch"/> заявок, каждая — своей транзакцией (заявка уходит вместе со своими назначениями,
    /// историей и outbox): после суток демо-потока это сотни тысяч назначений, и одной командой их удаление
    /// не укладывалось в таймаут. Возвращает номера удалённых заявок — их надо забыть и в хранилище нагрузки.
    /// </summary>
    public static async Task<IReadOnlyList<long>> WipeOrdersAsync(IBalancerDbContext db, int departmentId,
        CancellationToken cancellationToken)
    {
        var removed = new List<long>();
        while (true)
        {
            var batch = await db.Orders.AsNoTracking().Where(o => o.DepartmentId == departmentId)
                .OrderBy(o => o.Id).Select(o => o.Id).Take(WipeBatch).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await db.OutboxMessages.Where(m => batch.Contains(m.OrderId)).ExecuteDeleteAsync(cancellationToken);
                await db.OrderStatusChanges.Where(c => batch.Contains(c.OrderId)).ExecuteDeleteAsync(cancellationToken);
                await db.Assignments.Where(a => batch.Contains(a.OrderId)).ExecuteDeleteAsync(cancellationToken);
                await db.Orders.Where(o => batch.Contains(o.Id)).ExecuteDeleteAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            removed.AddRange(batch);
        }

        await db.ExecutorHourStats.Where(x => x.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        await db.EligibilityHourStats.Where(x => x.DepartmentId == departmentId).ExecuteDeleteAsync(cancellationToken);
        return removed;
    }
}
