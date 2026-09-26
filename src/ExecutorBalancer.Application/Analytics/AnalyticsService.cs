using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Application.Analytics;

public enum AnalyticsPeriod
{
    /// <summary>С полуночи по часовому поясу балансировщика, по часам.</summary>
    Today,

    /// <summary>Последние 24 часа, по часам.</summary>
    Day,

    /// <summary>Последние 7 суток, по 6 часов.</summary>
    Week,

    /// <summary>Последние 30 суток, по суткам.</summary>
    Month,
}

public sealed record TimelinePoint(
    DateTimeOffset Start,
    int Assigned,
    decimal AssignedWeight,
    int Closed,
    int Returned);

public sealed record ExecutorMetrics(
    long Id,
    string Name,
    bool IsActive,
    decimal Qualification,
    int Assigned,
    decimal AssignedWeight,
    int Primary,
    int Reassign,
    int Parent,
    int Secondary,
    decimal FreeWeight,
    decimal FairWeight,
    decimal? DeviationPercent,
    int Closed,
    int Returned,
    decimal? ReturnRatePercent);

/// <param name="MeanAbsDeviationPercent">Σ|назначенный вес − справедливый вес| / Σ справедливых весов, в процентах.</param>
/// <param name="MaxAbsDeviationPercent">Наибольшее отклонение у одного исполнителя (среди достаточно загруженных).</param>
public sealed record FairnessSummary(
    decimal? MeanAbsDeviationPercent,
    decimal? MaxAbsDeviationPercent,
    int ExecutorsMeasured,
    decimal FreeWeight);

public sealed record KindBreakdown(int Primary, int Reassign, int Parent, int Secondary);

public sealed record AnalyticsReport(
    AnalyticsPeriod Period,
    DateTimeOffset From,
    DateTimeOffset To,
    int BucketHours,
    IReadOnlyList<TimelinePoint> Timeline,
    IReadOnlyList<ExecutorMetrics> Executors,
    KindBreakdown Kinds,
    FairnessSummary Fairness);

public sealed record LivePoint(DateTimeOffset Minute, int Assigned);

/// <summary>
/// Отчёты по сводным таблицам executor_hour_stats и eligibility_hour_stats.
/// Заявки от родителя и вторичные обязаны идти «своему» исполнителю — в эталоне (<see cref="FairShare"/>)
/// они неизменная часть нагрузки, а выравнивается свободный выбор. Эталон строится отдельно для каждого интервала,
/// чтобы учитывать, кто в это время работал: ушедший на обед исполнитель не должен «догонять» утро.
/// </summary>
public sealed class AnalyticsService(
    IBalancerDbContext db,
    ExecutorDirectory directory,
    IOptions<BalancerOptions> options,
    TimeProvider clock)
{
    /// <summary>
    /// Отклонение в процентах имеет смысл, когда исполнителю причиталось хотя бы столько веса:
    /// на двух-трёх заявках одна лишняя даёт десятки процентов.
    /// </summary>
    public const decimal MinFairWeightForDeviation = 10m;

    public const int LiveMinutes = 30;

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public async Task<AnalyticsReport> BuildAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var currentHour = ExecutorStats.HourOf(now);
        var offset = (long)Math.Floor(_timeZone.GetUtcOffset(now).TotalHours);
        var (size, count) = period switch
        {
            AnalyticsPeriod.Today => (1, (int)((currentHour + offset) % 24) + 1),
            AnalyticsPeriod.Day => (1, 24),
            AnalyticsPeriod.Week => (6, 28),
            AnalyticsPeriod.Month => (24, 30),
            _ => throw new ArgumentOutOfRangeException(nameof(period)),
        };

        // корзины выровнены по местному времени: сутки — от полуночи, шесть часов — от 0, 6, 12, 18
        var lastKey = FloorDiv(currentHour + offset, size);
        var firstKey = lastKey - count + 1;
        var fromHour = firstKey * size - offset;

        var rows = await db.ExecutorHourStats.AsNoTracking()
            .Where(s => s.BucketHour >= fromHour && s.BucketHour <= currentHour)
            .ToListAsync(cancellationToken);
        var pools = await db.EligibilityHourStats.AsNoTracking()
            .Where(s => s.BucketHour >= fromHour && s.BucketHour <= currentHour)
            .ToListAsync(cancellationToken);

        var timeline = new TimelinePoint[count];
        for (var i = 0; i < count; i++)
        {
            timeline[i] = new TimelinePoint(ExecutorStats.HourStart((firstKey + i) * size - offset), 0, 0, 0, 0);
        }

        foreach (var row in rows)
        {
            var index = (int)(FloorDiv(row.BucketHour + offset, size) - firstKey);
            var point = timeline[index];
            timeline[index] = point with
            {
                Assigned = point.Assigned + row.AssignedCount,
                AssignedWeight = point.AssignedWeight + row.AssignedWeight,
                Closed = point.Closed + row.ClosedCount,
                Returned = point.Returned + row.ReturnedCount,
            };
        }

        var snapshot = await directory.GetAsync(cancellationToken);
        var qualification = snapshot.Executors.ToDictionary(e => e.Key, e => e.Value.QualificationWeight);
        var capped = LimitReached(rows, snapshot, offset, size);
        var fair = new Dictionary<long, decimal>();
        var poolsByWindow = pools.GroupBy(p => FloorDiv(p.BucketHour + offset, size)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var window in rows.GroupBy(r => FloorDiv(r.BucketHour + offset, size)))
        {
            // назначения без выбора — неизменная часть нагрузки, эталон выравнивает остальное с их учётом
            var forced = window.GroupBy(r => r.ExecutorId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.AssignedWeight - r.FreeWeight));
            var caps = window.GroupBy(r => r.ExecutorId)
                .Where(g => capped.Contains((g.Key, window.Key)))
                .ToDictionary(g => g.Key, g => g.Sum(r => r.AssignedWeight));
            var allocation = FairShare.Allocate(
                (poolsByWindow.GetValueOrDefault(window.Key) ?? [])
                    .GroupBy(p => p.SetKey)
                    .Select(g => new FairPool(ExecutorStats.ParseSetKey(g.Key), g.Sum(p => p.Weight))),
                qualification, forced, caps);
            foreach (var (id, weight) in allocation)
            {
                fair[id] = fair.GetValueOrDefault(id) + weight;
            }
        }

        var byExecutor = rows.GroupBy(r => r.ExecutorId).ToDictionary(g => g.Key, g => g.ToList());
        var ids = snapshot.Executors.Keys.Union(byExecutor.Keys).Order();
        var executors = ids
            .Select(id => Metrics(id, snapshot.Executors.GetValueOrDefault(id), byExecutor.GetValueOrDefault(id) ?? [],
                fair.GetValueOrDefault(id)))
            .Where(m => m.IsActive || m.Assigned > 0 || m.Closed > 0 || m.Returned > 0)
            .ToList();

        var kinds = new KindBreakdown(
            rows.Sum(r => r.PrimaryCount), rows.Sum(r => r.ReassignCount),
            rows.Sum(r => r.ParentCount), rows.Sum(r => r.SecondaryCount));

        return new AnalyticsReport(period, ExecutorStats.HourStart(fromHour), now, size, timeline, executors, kinds,
            Fairness(executors));
    }

    /// <summary>Назначения по минутам за последние полчаса — для живого графика на обзоре.</summary>
    public async Task<IReadOnlyList<LivePoint>> LiveAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lastMinute = FloorDiv(now.ToUnixTimeSeconds(), 60);
        var firstMinute = lastMinute - LiveMinutes + 1;
        var from = DateTimeOffset.FromUnixTimeSeconds(firstMinute * 60);

        var moments = await db.Assignments.AsNoTracking()
            .Where(a => a.CreatedAt >= from)
            .Select(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        var counts = new int[LiveMinutes];
        foreach (var moment in moments)
        {
            var index = FloorDiv(moment.ToUnixTimeSeconds(), 60) - firstMinute;
            if (index is >= 0 and < LiveMinutes)
            {
                counts[index]++;
            }
        }

        return counts
            .Select((count, i) => new LivePoint(DateTimeOffset.FromUnixTimeSeconds((firstMinute + i) * 60), count))
            .ToList();
    }

    /// <summary>
    /// Интервалы, в которых исполнитель набрал суточный лимит: там он больше получить не мог.
    /// Сутки — по местному времени, как у счётчика лимита.
    /// </summary>
    private static HashSet<(long ExecutorId, long Window)> LimitReached(List<ExecutorHourStat> rows,
        BalancerSnapshot snapshot, long offset, int size)
    {
        var result = new HashSet<(long, long)>();
        foreach (var day in rows.GroupBy(r => (r.ExecutorId, Day: FloorDiv(r.BucketHour + offset, 24))))
        {
            if (snapshot.Executors.GetValueOrDefault(day.Key.ExecutorId)?.DailyLimit is not { } limit)
            {
                continue;
            }

            var total = 0;
            foreach (var row in day.OrderBy(r => r.BucketHour))
            {
                total += row.AssignedCount;
                if (total >= limit)
                {
                    result.Add((row.ExecutorId, FloorDiv(row.BucketHour + offset, size)));
                }
            }
        }

        return result;
    }

    private static ExecutorMetrics Metrics(long id, ExecutorProfile? profile, List<ExecutorHourStat> rows,
        decimal fairWeight)
    {
        var assignedWeight = rows.Sum(r => r.AssignedWeight);
        var freeWeight = rows.Sum(r => r.FreeWeight);
        var closed = rows.Sum(r => r.ClosedCount);
        var returned = rows.Sum(r => r.ReturnedCount);
        decimal? deviation = fairWeight >= MinFairWeightForDeviation
            ? Math.Round((assignedWeight - fairWeight) / fairWeight * 100m, 2)
            : null;
        decimal? returnRate = closed + returned > 0
            ? Math.Round(returned * 100m / (closed + returned), 1)
            : null;

        return new ExecutorMetrics(
            id,
            profile?.FullName ?? $"#{id}",
            profile?.IsActive ?? false,
            profile?.QualificationWeight ?? 0,
            rows.Sum(r => r.AssignedCount),
            assignedWeight,
            rows.Sum(r => r.PrimaryCount),
            rows.Sum(r => r.ReassignCount),
            rows.Sum(r => r.ParentCount),
            rows.Sum(r => r.SecondaryCount),
            freeWeight,
            Math.Round(fairWeight, 3),
            deviation,
            closed,
            returned,
            returnRate);
    }

    private static FairnessSummary Fairness(IReadOnlyList<ExecutorMetrics> executors)
    {
        var totalFair = executors.Sum(e => e.FairWeight);
        var totalFree = executors.Sum(e => e.FreeWeight);
        var measured = executors.Where(e => e.DeviationPercent is not null).ToList();
        decimal? mean = totalFair >= MinFairWeightForDeviation
            ? Math.Round(executors.Sum(e => Math.Abs(e.AssignedWeight - e.FairWeight)) / totalFair * 100m, 2)
            : null;
        decimal? max = measured.Count > 0 ? measured.Max(e => Math.Abs(e.DeviationPercent!.Value)) : null;
        return new FairnessSummary(mean, max, measured.Count, totalFree);
    }

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
