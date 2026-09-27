using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>
/// Только в демо-режиме: после запуска заводит сотрудников в отделах, где их нет, — чтобы на показе
/// каждая сфера сразу была «живой». Ждёт, пока поднимется эмулятор АИС; в боевом режиме не регистрируется.
/// </summary>
public sealed class DemoWarmup(IServiceScopeFactory scopes, IOptions<DemoOptions> options, ILogger<DemoWarmup> logger)
    : BackgroundService
{
    public const int ExecutorsPerDepartment = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 180; // полчаса: эмулятор могут поднять позже основного сервиса

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            // видно в «docker compose logs api»: частая причина пустых отделов на показе — не включён демо-режим
            logger.LogInformation("Демо-режим выключен (DEMO_ENABLED): тестовые сотрудники не заводятся");
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                if (await SeedEmptyDepartmentsAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Заполнение демо-отделов: повторим");
            }

            if (attempt == 1)
            {
                logger.LogInformation("Демо: ждём эмулятор АИС, чтобы завести сотрудников (запущен ли он с COMPOSE_PROFILES=demo?)");
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }

        logger.LogWarning("Эмулятор АИС не ответил — демо-отделы остались без сотрудников; заведите их на тестовом стенде");
    }

    /// <summary>true — все отделы с сотрудниками (или заведены сейчас); false — АИС пока недоступна.</summary>
    private async Task<bool> SeedEmptyDepartmentsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBalancerDbContext>();
        var directory = scope.ServiceProvider.GetRequiredService<ExecutorDirectory>();
        var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(AisOptions.HttpClientName);
        var empty = await db.Departments.AsNoTracking()
            .Where(d => !db.Executors.Any(e => e.DepartmentId == d.Id))
            .OrderBy(d => d.Id).Select(d => new { d.Id, d.Name })
            .ToListAsync(ct);
        foreach (var department in empty)
        {
            var result = await DemoEndpoints.SeedAsync(department.Id, ExecutorsPerDepartment, db, client, directory, ct);
            if (result.Error is not null)
            {
                return false;
            }

            logger.LogInformation("Демо: в отдел «{Department}» заведено сотрудников: {Count}", department.Name,
                ExecutorsPerDepartment);
        }

        return true;
    }
}
