using ExecutorBalancer.Infrastructure.Persistence;
using ExecutorBalancer.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure.Workers;

/// <summary>
/// Сторож Redis во время работы. Нагрузка раньше восстанавливалась из базы только при старте сервиса,
/// и если Redis перезапускался без данных или со старым снимком, распределение вставало до перезапуска
/// (исполнители числились неактивными, суточные счётчики — чужими). Каждые 15 секунд сторож:
/// увеличивает счётчик-«метроном» — если он пропал или оказался меньше виденного, Redis откатился, и нагрузка
/// собирается заново из базы; и дописывает недостающие признаки активности исполнителей.
/// </summary>
internal sealed class RedisStateGuard(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    RedisStateRebuilder rebuilder,
    ILogger<RedisStateGuard> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private long lastEpoch;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Сверка Redis с базой не удалась, повторим");
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var cache = redis.GetDatabase();
        var epoch = await cache.StringIncrementAsync(RedisKeys.Epoch);
        var rewound = epoch <= lastEpoch;
        var lost = !await cache.KeyExistsAsync(RedisKeys.StateReady);
        lastEpoch = epoch;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BalancerDbContext>();
        if (rewound || lost)
        {
            logger.LogWarning("Redis {Reason} — восстанавливаем нагрузку из базы", lost ? "потерял данные" : "откатился к старому снимку");
            if (rewound)
            {
                // старый снимок: метка готовности в нём есть, но нагрузка устарела
                await cache.KeyDeleteAsync(RedisKeys.StateReady);
            }

            await rebuilder.RebuildOnceAsync(db, cancellationToken);
            await rebuilder.SyncActiveAsync(db, overwrite: true, cancellationToken);
            await cache.StringIncrementAsync(RedisKeys.ConfigVersion);
            return;
        }

        await rebuilder.SyncActiveAsync(db, overwrite: false, cancellationToken);
    }
}
