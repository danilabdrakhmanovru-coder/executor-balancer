using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using ExecutorBalancer.Infrastructure.Persistence;
using ExecutorBalancer.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure;

/// <summary>
/// При старте: миграции, стартовая конфигурация и сверка Redis с базой.
/// Если Redis пустой (перезапуск без сохранённых данных), нагрузка восстанавливается
/// из PostgreSQL. Восстановлением занимается один экземпляр — под блокировкой.
/// </summary>
internal sealed class StartupInitializer(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    IOptions<BalancerOptions> options,
    TimeProvider clock,
    ILogger<StartupInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BalancerDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await DefaultConfiguration.SeedAsync(db, cancellationToken);

        var cache = redis.GetDatabase();
        if (!await cache.KeyExistsAsync(RedisKeys.StateReady))
        {
            await RebuildOnceAsync(db, cache, cancellationToken);
        }

        var executors = await db.Executors.AsNoTracking().Select(e => new { e.Id, e.IsActive }).ToListAsync(cancellationToken);
        if (executors.Count > 0)
        {
            await cache.HashSetAsync(RedisKeys.Active,
                executors.Select(e => new HashEntry(e.Id, e.IsActive ? 1 : 0)).ToArray());
        }

        await cache.StringIncrementAsync(RedisKeys.ConfigVersion);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RebuildOnceAsync(BalancerDbContext db, IDatabase cache, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        if (!await cache.LockTakeAsync(RedisKeys.RebuildLock, token, TimeSpan.FromMinutes(1)))
        {
            // восстановлением занимается другой экземпляр — ждём
            for (var i = 0; i < 60 && !await cache.KeyExistsAsync(RedisKeys.StateReady); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            return;
        }

        try
        {
            await RebuildAsync(db, cache, cancellationToken);
        }
        finally
        {
            await cache.LockReleaseAsync(RedisKeys.RebuildLock, token);
        }
    }

    private async Task RebuildAsync(BalancerDbContext db, IDatabase cache, CancellationToken cancellationToken)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        var day = DateOnly.FromDateTime(localNow.DateTime);
        var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset).ToUniversalTime();

        var open = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Processed && o.ExecutorId != null)
            .Select(o => new { o.Id, ExecutorId = o.ExecutorId!.Value, o.Weight })
            .ToListAsync(cancellationToken);
        var today = await db.Assignments.AsNoTracking()
            .Where(a => a.CreatedAt >= dayStart && a.Kind != AssignmentKind.Secondary) // возвраты с доработки — не новые заявки
            .GroupBy(a => a.ExecutorId)
            .Select(g => new { ExecutorId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var batch = cache.CreateTransaction();
        _ = batch.KeyDeleteAsync([RedisKeys.OpenWeight, RedisKeys.OpenCount, RedisKeys.Daily(day)]);
        foreach (var group in open.GroupBy(o => o.ExecutorId))
        {
            _ = batch.HashSetAsync(RedisKeys.OpenWeight, group.Key, group.Sum(o => LoadMath.ToMilli(o.Weight)));
            _ = batch.HashSetAsync(RedisKeys.OpenCount, group.Key, group.Count());
        }

        foreach (var order in open)
        {
            _ = batch.HashSetAsync(RedisKeys.Order(order.Id),
            [
                new HashEntry("executor", order.ExecutorId),
                new HashEntry("weight", LoadMath.ToMilli(order.Weight)),
                new HashEntry("state", "open"),
            ]);
        }

        foreach (var row in today)
        {
            _ = batch.HashSetAsync(RedisKeys.Daily(day), row.ExecutorId, row.Count);
        }

        _ = batch.KeyExpireAsync(RedisKeys.Daily(day), TimeSpan.FromDays(3));
        _ = batch.StringSetAsync(RedisKeys.StateReady, clock.GetUtcNow().ToString("O"));
        await batch.ExecuteAsync();

        logger.LogInformation("Нагрузка в Redis восстановлена из базы: открытых заявок {Open}, назначений за сутки {Today}",
            open.Count, today.Sum(t => t.Count));
    }
}
