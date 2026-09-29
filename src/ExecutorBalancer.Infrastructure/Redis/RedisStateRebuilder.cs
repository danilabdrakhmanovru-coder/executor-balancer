using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using ExecutorBalancer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure.Redis;

/// <summary>
/// Восстановление нагрузки в Redis из PostgreSQL (база — источник правды): при старте и когда
/// <see cref="Workers.RedisStateGuard"/> заметил, что Redis потерял данные или откатился к старому снимку.
/// Восстановлением занимается один экземпляр — под блокировкой.
/// </summary>
internal sealed class RedisStateRebuilder(
    IConnectionMultiplexer redis,
    IOptions<BalancerOptions> options,
    TimeProvider clock,
    ILogger<RedisStateRebuilder> logger)
{
    /// <summary>
    /// Признак активности исполнителей из базы. <paramref name="overwrite"/> = false дописывает только недостающие —
    /// так периодическая сверка не перетрёт свежее изменение, сделанное между чтением базы и записью.
    /// </summary>
    public async Task SyncActiveAsync(BalancerDbContext db, bool overwrite, CancellationToken cancellationToken)
    {
        var executors = await db.Executors.AsNoTracking().Select(e => new { e.Id, e.IsActive }).ToListAsync(cancellationToken);
        if (executors.Count == 0)
        {
            return;
        }

        var cache = redis.GetDatabase();
        if (overwrite)
        {
            await cache.HashSetAsync(RedisKeys.Active,
                executors.Select(e => new HashEntry(e.Id, e.IsActive ? 1 : 0)).ToArray());
            return;
        }

        var batch = cache.CreateBatch();
        var added = executors.Select(e => batch.HashSetAsync(RedisKeys.Active, e.Id, e.IsActive ? 1 : 0, When.NotExists)).ToList();
        batch.Execute();
        var restored = (await Task.WhenAll(added)).Count(x => x);
        if (restored > 0)
        {
            logger.LogWarning("В Redis не хватало признака активности у {Count} исполнителей — восстановлен из базы", restored);
        }
    }

    public async Task RebuildOnceAsync(BalancerDbContext db, CancellationToken cancellationToken)
    {
        var cache = redis.GetDatabase();
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
        var hour = ExecutorStats.HourOf(clock.GetUtcNow());
        var hourStart = ExecutorStats.HourStart(hour);
        var thisHour = await db.Assignments.AsNoTracking()
            .Where(a => a.CreatedAt >= hourStart)
            .GroupBy(a => a.ExecutorId)
            .Select(g => new { ExecutorId = g.Key, Weight = g.Sum(a => a.OrderWeight) })
            .ToListAsync(cancellationToken);
        var today = await db.Assignments.AsNoTracking()
            .Where(a => a.CreatedAt >= dayStart && a.Kind != AssignmentKind.Secondary) // возвраты с доработки — не новые заявки
            .GroupBy(a => a.ExecutorId)
            .Select(g => new { ExecutorId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var batch = cache.CreateTransaction();
        _ = batch.KeyDeleteAsync([RedisKeys.OpenWeight, RedisKeys.OpenCount, RedisKeys.Daily(day), RedisKeys.HourWeight(hour)]);
        foreach (var row in thisHour)
        {
            _ = batch.HashSetAsync(RedisKeys.HourWeight(hour), row.ExecutorId, LoadMath.ToMilli(row.Weight));
        }

        _ = batch.KeyExpireAsync(RedisKeys.HourWeight(hour), TimeSpan.FromHours(2));
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
