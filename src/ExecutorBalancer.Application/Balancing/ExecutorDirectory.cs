using System.Collections.Concurrent;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Application.Balancing;

/// <summary>
/// Кеш конфигурации в памяти экземпляра, по отделам. Раз в ConfigCheckInterval сверяет версию в Redis:
/// если правила или исполнители менялись (на любом экземпляре) — перечитывает из базы.
/// Активность исполнителя дополнительно проверяется атомарно при выборе, поэтому
/// устаревший на долю секунды кеш не приводит к назначению неактивному.
/// </summary>
public sealed class ExecutorDirectory(
    IServiceScopeFactory scopes,
    ILoadStore loadStore,
    IOptions<BalancerOptions> options,
    TimeProvider clock,
    ILogger<ExecutorDirectory> logger) : IDisposable
{
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private readonly ConcurrentDictionary<int, BalancerSnapshot> _snapshots = new();
    private long _version = -1;
    private long _checkedAtTicks;

    /// <summary>Срез конфигурации отдела: справочник, правила, веса и сотрудники этого отдела.</summary>
    public async Task<BalancerSnapshot> GetAsync(int departmentId, CancellationToken cancellationToken)
    {
        if (IsFresh() && _snapshots.TryGetValue(departmentId, out var snapshot))
        {
            return snapshot;
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsFresh())
            {
                // версия общая для всех отделов: любое изменение сбрасывает кеш целиком
                var version = await loadStore.GetConfigVersionAsync(cancellationToken);
                if (version != Interlocked.Read(ref _version))
                {
                    _snapshots.Clear();
                    Interlocked.Exchange(ref _version, version);
                }

                Volatile.Write(ref _checkedAtTicks, clock.GetUtcNow().UtcTicks);
            }

            if (!_snapshots.TryGetValue(departmentId, out snapshot))
            {
                snapshot = await LoadAsync(departmentId, Interlocked.Read(ref _version), cancellationToken);
                _snapshots[departmentId] = snapshot;
            }

            return snapshot;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    public void Dispose() => _reloadGate.Dispose();

    public void Invalidate() => Volatile.Write(ref _checkedAtTicks, 0);

    private bool IsFresh() =>
        clock.GetUtcNow().UtcTicks - Volatile.Read(ref _checkedAtTicks) < options.Value.ConfigCheckInterval.Ticks;

    private async Task<BalancerSnapshot> LoadAsync(int departmentId, long version, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBalancerDbContext>();

        var catalog = new FieldCatalog(await db.FieldDefinitions.AsNoTracking()
            .Where(f => f.DepartmentId == departmentId)
            .ToListAsync(cancellationToken));

        var rules = new List<CompiledRule>();
        var errors = new Dictionary<int, string>();
        var ruleRows = await db.Rules.AsNoTracking()
            .Where(r => r.IsEnabled && r.DepartmentId == departmentId)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var row in ruleRows)
        {
            try
            {
                rules.Add(RuleCompiler.Compile(row, catalog));
            }
            catch (RuleValidationException ex)
            {
                errors[row.Id] = ex.Message;
                logger.LogWarning("Правило {RuleId} не применяется: {Reason}", row.Id, ex.Message);
            }
        }

        var weightRules = new List<CompiledWeightRule>();
        var weightRows = await db.WeightRules.AsNoTracking()
            .Where(r => r.IsEnabled && r.DepartmentId == departmentId)
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var row in weightRows)
        {
            try
            {
                weightRules.Add(RuleCompiler.CompileWeight(row, catalog));
            }
            catch (RuleValidationException ex)
            {
                logger.LogWarning("Правило веса {RuleId} не применяется: {Reason}", row.Id, ex.Message);
            }
        }

        var executors = await db.Executors.AsNoTracking()
            .Where(e => e.DepartmentId == departmentId)
            .ToListAsync(cancellationToken);
        var profiles = executors.ToDictionary(
            e => e.Id,
            e => new ExecutorProfile(e.Id, e.FullName, e.IsActive, e.DailyLimit, e.QualificationWeight,
                catalog.ParseStored(FieldOwner.Executor, e.AttributesJson)));

        return new BalancerSnapshot
        {
            DepartmentId = departmentId,
            Version = version,
            Catalog = catalog,
            Rules = rules,
            RuleErrors = errors,
            WeightRules = weightRules,
            Executors = profiles,
            DefaultOrderWeight = options.Value.DefaultOrderWeight,
        };
    }
}
