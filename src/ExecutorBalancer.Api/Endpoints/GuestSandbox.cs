using System.Collections.Concurrent;
using ExecutorBalancer.Api.Security;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Insights;
using ExecutorBalancer.Application.Users;
using ExecutorBalancer.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>Когда гость последний раз обращался к сайту — по его песочнице. Пока вкладка открыта, интерфейс обновляется
/// каждые пару секунд, так что «давно не было» значит «гость ушёл».</summary>
public sealed class GuestActivity
{
    private readonly ConcurrentDictionary<int, DateTimeOffset> _seen = new();

    public void Touch(int departmentId, DateTimeOffset now) => _seen[departmentId] = now;

    /// <summary>Песочницу, которую ещё не видели (сервис перезапущен), считаем активной с этого момента.</summary>
    public DateTimeOffset LastSeen(int departmentId, DateTimeOffset now) => _seen.GetOrAdd(departmentId, now);

    public void Forget(int departmentId) => _seen.TryRemove(departmentId, out _);
}

/// <summary>
/// Песочница гостя: при гостевом входе с правами руководителя создаётся свой отдел по шаблону сферы, с сотрудниками и
/// потоком заявок. Его видит только этот гость — у каждого своя история, гости не мешают друг другу. Уходит гость
/// (выход или долгое бездействие) — песочница удаляется целиком, общие отделы не меняются.
/// </summary>
public static class GuestSandbox
{
    private static readonly TimeSpan StaffWait = TimeSpan.FromSeconds(5);

    /// <summary>Создаёт песочницу и оживляет её: сотрудники в АИС и поток заявок. Возвращает номер отдела.</summary>
    internal static async Task<int> CreateAsync(IServiceProvider services, CancellationToken ct)
    {
        var options = services.GetRequiredService<IOptions<DemoOptions>>().Value;
        var clock = services.GetRequiredService<TimeProvider>();
        var db = services.GetRequiredService<IBalancerDbContext>();
        var directory = services.GetRequiredService<ExecutorDirectory>();
        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(AisOptions.HttpClientName);
        var preset = DomainPresets.Find(options.GuestPreset) ?? DomainPresets.Bank;
        var department = await services.GetRequiredService<DepartmentService>()
            .CreateGuestAsync(preset, options.MaxGuests, ct);
        services.GetRequiredService<GuestActivity>().Touch(department.Id, clock.GetUtcNow());

        var staff = DemoWarmup.ExecutorsPerDepartment;
        if ((await DemoEndpoints.SeedAsync(department.Id, staff, db, client, directory, ct)).Error is not null)
        {
            return department.Id; // эмулятор недоступен — отдел есть, сотрудников гость заведёт позже или без них
        }

        // АИС передаёт сотрудников балансировщику сама — ждём их, чтобы первые заявки сразу нашли исполнителя
        var until = clock.GetUtcNow() + StaffWait;
        while (clock.GetUtcNow() < until
               && await db.Executors.CountAsync(e => e.DepartmentId == department.Id, ct) < staff)
        {
            await Task.Delay(200, ct);
        }

        if (options.GuestFlowRatePerHour > 0)
        {
            await DemoEndpoints.StartFlowAsync(client, db, directory, services.GetRequiredService<ICurrentActor>(), clock,
                department.Id, Math.Min(options.GuestFlowRatePerHour, options.ManagerMaxRatePerHour),
                options.ManagerFlowMinutes > 0 ? options.ManagerFlowMinutes : null, ct);
        }

        return department.Id;
    }

    /// <summary>Удаляет песочницу: поток и данные в АИС, затем всё в балансировщике. Обычные отделы не трогает.</summary>
    internal static async Task DeleteAsync(IServiceProvider services, int departmentId, CancellationToken ct)
    {
        var db = services.GetRequiredService<IBalancerDbContext>();
        var code = await db.Departments.AsNoTracking().Where(d => d.Id == departmentId && d.IsGuest)
            .Select(d => d.Code).FirstOrDefaultAsync(ct);
        if (code is null)
        {
            return;
        }

        var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(AisOptions.HttpClientName);
        await DemoEndpoints.RelayResetAsync(client, code, ct);
        var executors = await services.GetRequiredService<DepartmentService>().DeleteGuestAsync(departmentId, ct) ?? [];
        foreach (var executor in executors)
        {
            await DemoEndpoints.MirrorStaffAsync(client, db, departmentId, executor, null, ct);
        }

        services.GetRequiredService<AiGate>().Forget(departmentId);
        services.GetRequiredService<GuestActivity>().Forget(departmentId);
    }
}

/// <summary>
/// Удаляет песочницы гостей, которые ушли: GuestResetMinutes (30) без единого обращения к сайту. Работает только
/// в демо-режиме.
/// </summary>
public sealed class GuestSandboxCleanup(IServiceScopeFactory scopes, IOptions<DemoOptions> demo, GuestActivity activity,
    TimeProvider clock, ILogger<GuestSandboxCleanup> logger) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!demo.Value.Enabled)
        {
            return;
        }

        var idle = TimeSpan.FromMinutes(Math.Max(1, demo.Value.GuestResetMinutes));
        using var timer = new PeriodicTimer(CheckEvery, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IBalancerDbContext>();
                var sandboxes = await db.Departments.AsNoTracking().Where(d => d.IsGuest).Select(d => d.Id)
                    .ToListAsync(stoppingToken);
                var now = clock.GetUtcNow();
                foreach (var id in sandboxes.Where(id => now - activity.LastSeen(id, now) >= idle))
                {
                    await GuestSandbox.DeleteAsync(scope.ServiceProvider, id, stoppingToken);
                    logger.LogInformation("Песочница гостя {Id} удалена: гость ушёл", id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Удаление песочниц гостей не удалось — попробуем через минуту");
            }
        }
    }
}
