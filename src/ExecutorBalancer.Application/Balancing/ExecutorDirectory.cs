using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Application.Balancing;

/// <summary>
/// Кеш конфигурации в памяти экземпляра. Раз в ConfigCheckInterval сверяет версию в Redis:
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
    private volatile BalancerSnapshot? _snapshot;
    private long _checkedAtTicks;

    public async Task<BalancerSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot is not null && IsFresh())
        {
            return snapshot;
        }

        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && IsFresh())
            {
                return snapshot;
            }

            var version = await loadStore.GetConfigVersionAsync(cancellationToken);
            if (snapshot is null || snapshot.Version != version)
            {
                snapshot = await LoadAsync(version, cancellationToken);
                _snapshot = snapshot;
            }

            Volatile.Write(ref _checkedAtTicks, clock.GetUtcNow().UtcTicks);
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

    private async Task<BalancerSnapshot> LoadAsync(long version, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBalancerDbContext>();

        var catalog = new FieldCatalog(await db.FieldDefinitions.AsNoTracking().ToListAsync(cancellationToken));

        var rules = new List<CompiledRule>();
        var errors = new Dictionary<int, string>();
        var ruleRows = await db.Rules.AsNoTracking()
            .Where(r => r.IsEnabled)
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
            .Where(r => r.IsEnabled)
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

        var executors = await db.Executors.AsNoTracking().ToListAsync(cancellationToken);
        var profiles = executors.ToDictionary(
            e => e.Id,
            e => new ExecutorProfile(e.Id, e.FullName, e.IsActive, e.DailyLimit, e.QualificationWeight,
                catalog.ParseStored(FieldOwner.Executor, e.AttributesJson)));

        return new BalancerSnapshot
        {
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
