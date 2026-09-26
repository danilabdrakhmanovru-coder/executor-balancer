using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using ExecutorBalancer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Infrastructure.Workers;

/// <summary>
/// Заявки без исполнителя (все были заняты или неактивны) периодически пробуем распределить снова.
/// Двойного назначения при работе нескольких экземпляров не будет: выбор атомарен в Redis.
/// </summary>
internal sealed class PendingRetryWorker(
    IServiceScopeFactory scopes,
    IOptions<AisOptions> options,
    ILogger<PendingRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.Value.PendingRetryInterval, stoppingToken);
            try
            {
                await RetryAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ошибка повторного распределения");
            }
        }
    }

    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        List<long> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BalancerDbContext>();
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-2);
            ids = await db.Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.Processed && o.ExecutorId == null && o.ReceivedAt < cutoff)
                .OrderBy(o => o.ReceivedAt)
                .Select(o => o.Id)
                .Take(200)
                .ToListAsync(cancellationToken);
        }

        var assigned = 0;
        foreach (var id in ids)
        {
            await using var scope = scopes.CreateAsyncScope();
            var balancer = scope.ServiceProvider.GetRequiredService<OrderBalancer>();
            var result = await balancer.RetryPendingAsync(id, cancellationToken);
            if (result is { Outcome: BalanceOutcome.Assigned, Duplicate: false })
            {
                assigned++;
            }
        }

        if (assigned > 0)
        {
            logger.LogInformation("Распределено ранее ожидавших заявок: {Assigned} из {Total}", assigned, ids.Count);
        }
    }
}
