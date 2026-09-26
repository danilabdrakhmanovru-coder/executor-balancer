using System.Collections.Concurrent;
using ExecutorBalancer.Application.Balancing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExecutorBalancer.Application.Analytics;

/// <summary>
/// Качество работы сотрудников за последние <see cref="Motivation.QualityWindowDays"/> дней —
/// из почасовой статистики, без разбора истории заявок. Нужно при каждом выборе сверх нормы,
/// поэтому держится в памяти и перечитывается не чаще раза в <see cref="Ttl"/>.
/// </summary>
public sealed class QualityTracker(IServiceScopeFactory scopes, TimeProvider clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, (DateTimeOffset At, IReadOnlyDictionary<long, QualityInfo> Data)> _cache = new();

    public async Task<IReadOnlyDictionary<long, QualityInfo>> GetAsync(int departmentId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(departmentId, out var cached) && now - cached.At < Ttl)
        {
            return cached.Data;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBalancerDbContext>();
        var data = await LoadAsync(db, departmentId, now.AddDays(-Motivation.QualityWindowDays), cancellationToken);
        _cache[departmentId] = (now, data);
        return data;
    }

    /// <summary>Сбросить кэш — после закрытия заявок в тестах и при смене настроек.</summary>
    public void Invalidate() => _cache.Clear();

    public static async Task<IReadOnlyDictionary<long, QualityInfo>> LoadAsync(IBalancerDbContext db, int departmentId,
        DateTimeOffset from, CancellationToken cancellationToken)
    {
        var fromBucket = ExecutorStats.HourOf(from);
        // суммы считаются в памяти: строк немного (часы × сотрудники), а SQLite в тестах не суммирует decimal
        var rows = (await db.ExecutorHourStats.AsNoTracking()
                .Where(s => s.DepartmentId == departmentId && s.BucketHour >= fromBucket)
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.ExecutorId)
            .Select(g => new
            {
                ExecutorId = g.Key,
                Closed = g.Sum(s => s.ClosedCount),
                ClosedWeight = g.Sum(s => s.ClosedWeight),
                Points = g.Sum(s => s.Points),
                FastClosed = g.Sum(s => s.FastClosedCount),
                Returned = g.Sum(s => s.ReturnedCount),
            });
        return rows.ToDictionary(r => r.ExecutorId, r => new QualityInfo(r.Closed, r.ClosedWeight, r.Points,
            r.FastClosed, r.Returned, Motivation.QualityFrom(r.Closed, r.ClosedWeight, r.Points)));
    }
}
