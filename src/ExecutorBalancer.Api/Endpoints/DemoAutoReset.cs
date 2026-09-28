using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>
/// Демо с гостевым входом: если гость что-то менял (перерывы, увольнения, «больше нормы») и GuestResetMinutes (по умолчанию
/// полчаса) больше ничего не делал, демо само возвращается в исходное — следующий посетитель видит нормальную картину. Заявки и статистика
/// остаются. Работает, только когда включены и демо-режим, и гостевой вход.
/// </summary>
public sealed class DemoAutoReset(IServiceScopeFactory scopes, IOptions<DemoOptions> demo, IOptions<AdminOptions> admin,
    TimeProvider clock, ILogger<DemoAutoReset> logger) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!demo.Value.Enabled || admin.Value.Guest is null || demo.Value.GuestResetMinutes <= 0)
        {
            return;
        }

        var quiet = TimeSpan.FromMinutes(demo.Value.GuestResetMinutes);
        // изменения гостей до запуска сервиса уже не наши: считаем от момента запуска
        var lastReset = clock.GetUtcNow();
        using var timer = new PeriodicTimer(CheckEvery, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IBalancerDbContext>();
                var lastGuestChange = await db.AuditEntries.AsNoTracking()
                    .Where(a => a.Actor == BuiltInAdmin.GuestLogin && a.Action != "login" && a.Action != "demo_reset")
                    .MaxAsync(a => (DateTimeOffset?)a.CreatedAt, stoppingToken);
                var now = clock.GetUtcNow();
                if (lastGuestChange is not { } changed || changed <= lastReset || now - changed < quiet)
                {
                    continue;
                }

                var (back, added, orders) = await DemoEndpoints.ResetDemoAsync(scope.ServiceProvider, _ => true, automatic: true,
                    stoppingToken);
                lastReset = now;
                logger.LogInformation(
                    "Демо возвращено в исходное после гостей: удалено заявок {Orders}, на работу вернулись {Back}, заведено {Added}",
                    orders, back, added);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Автосброс демо не удался — попробуем через минуту");
            }
        }
    }
}
