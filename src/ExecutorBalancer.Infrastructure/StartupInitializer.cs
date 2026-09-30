using ExecutorBalancer.Infrastructure.Persistence;
using ExecutorBalancer.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure;

/// <summary>
/// При старте: миграции, стартовая конфигурация и сверка Redis с базой.
/// Если Redis пустой (перезапуск без сохранённых данных), нагрузка восстанавливается
/// из PostgreSQL (<see cref="RedisStateRebuilder"/>). Дальше за Redis следит <see cref="Workers.RedisStateGuard"/>.
/// </summary>
internal sealed class StartupInitializer(
    IServiceScopeFactory scopes,
    IConnectionMultiplexer redis,
    RedisStateRebuilder rebuilder) : IHostedService
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
            await rebuilder.RebuildOnceAsync(db, cancellationToken);
        }

        await rebuilder.SyncActiveAsync(db, overwrite: true, cancellationToken);
        await cache.StringIncrementAsync(RedisKeys.ConfigVersion);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
