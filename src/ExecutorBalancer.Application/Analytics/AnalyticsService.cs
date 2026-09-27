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
    decimal? ReturnRatePercent,
    decimal ClosedWeight = 0,
    decimal Points = 0,
    decimal? Quality = null,
    int FastClosed = 0,
    int Extra = 0,
    int? Rank = null);

/// <param name="MeanAbsDeviationPercent">Σ|назначенный вес − справедливый вес| / Σ справедливых весов, в процентах.</param>
/// <param name="MaxAbsDeviationPercent">Наибольшее отклонение у одного исполнителя (среди достаточно загруженных).</param>
public sealed record FairnessSummary(
    decimal? MeanAbsDeviationPercent,
    decimal? MaxAbsDeviationPercent,
    int ExecutorsMeasured,
    decimal FreeWeight);

public sealed record KindBreakdown(int Primary, int Reassign, int Parent, int Secondary, int Extra = 0);

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
/// они неизменная часть нагрузки, а выравнивается свободный выбор. Эталон строится по часам, как и сам выбор
/// (главный критерий — вес, полученный за текущий час), а внутри часа — по пятиминуткам по порядку: учитывается,
/// кто в это время работал и кто уже упёрся в суточный лимит. Эталон не требует от выбора предвидения — например,
/// заранее «приберечь» заявки для коллеги, который через десять минут останется единственным подходящим.
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

    public async Task<AnalyticsReport> BuildAsync(int departmentId, AnalyticsPeriod period,
        CancellationToken cancellationToken)
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
            .Where(s => s.DepartmentId == departmentId && s.BucketHour >= fromHour && s.BucketHour <= currentHour)
            .ToListAsync(cancellationToken);
        var pools = await db.EligibilityHourStats.AsNoTracking()
            .Where(s => s.DepartmentId == departmentId && s.BucketHour >= fromHour && s.BucketHour <= currentHour)
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

        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var qualificationAt = await QualificationHistoryAsync(snapshot, rows, pools, cancellationToken);
        var capped = LimitReached(rows, snapshot, offset);
        var fair = new Dictionary<long, decimal>();
        var poolsByHour = pools.GroupBy(p => p.BucketHour).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var hour in rows.GroupBy(r => r.BucketHour))
        {
            var allocation = FairHour(hour.ToList(), poolsByHour.GetValueOrDefault(hour.Key) ?? [],
                // квалификация — та, что действовала в этот час: смена днём не искажает утренние часы
                qualificationAt(ExecutorStats.HourStart(hour.Key).AddMinutes(30)), capped);
            foreach (var (id, weight) in allocation)
            {
                fair[id] = fair.GetValueOrDefault(id) + weight;
            }
        }

        var byExecutor = rows.GroupBy(r => r.ExecutorId).ToDictionary(g => g.Key, g => g.ToList());
        var ids = snapshot.Executors.Keys.Union(byExecutor.Keys).Order();
        List<ExecutorMetrics> executors = ids
            .Select(id => Metrics(id, snapshot.Executors.GetValueOrDefault(id), byExecutor.GetValueOrDefault(id) ?? [],
                fair.GetValueOrDefault(id)))
            .Where(m => m.IsActive || m.Assigned > 0 || m.Closed > 0 || m.Returned > 0)
            .ToList();

        executors = RankByPoints(executors, snapshot.Motivation);

        var kinds = new KindBreakdown(
            rows.Sum(r => r.PrimaryCount), rows.Sum(r => r.ReassignCount),
            rows.Sum(r => r.ParentCount), rows.Sum(r => r.SecondaryCount), rows.Sum(r => r.ExtraCount));

        return new AnalyticsReport(period, ExecutorStats.HourStart(fromHour), now, size, timeline, executors, kinds,
            Fairness(executors));
    }

    /// <summary>
    /// Квалификация сотрудников на момент времени — по истории изменений (executor_qualifications).
    /// Нет истории — текущее значение; момент раньше первой записи — первое известное значение.
    /// </summary>
    private async Task<Func<DateTimeOffset, IReadOnlyDictionary<long, decimal>>> QualificationHistoryAsync(
        BalancerSnapshot snapshot, List<ExecutorHourStat> rows, List<EligibilityHourStat> pools, CancellationToken cancellationToken)
    {
        var current = snapshot.Executors.ToDictionary(e => e.Key, e => e.Value.QualificationWeight);
        var ids = rows.Select(r => r.ExecutorId)
            .Concat(pools.SelectMany(p => ExecutorStats.ParseSetKey(p.SetKey)))
            .Distinct()
            .ToList();
        var history = (await db.ExecutorQualifications.AsNoTracking()
                .Where(q => ids.Contains(q.ExecutorId))
                .ToListAsync(cancellationToken))
            .GroupBy(q => q.ExecutorId)
            .ToDictionary(g => g.Key, g => g.OrderBy(q => q.ValidFrom).ThenBy(q => q.Id).ToList());
        if (history.Count == 0)
        {
            return _ => current;
        }

        return moment =>
        {
            var result = new Dictionary<long, decimal>(current);
            foreach (var (id, changes) in history)
            {
                result[id] = (changes.LastOrDefault(q => q.ValidFrom <= moment) ?? changes[0]).Qualification;
            }

            return result;
        };
    }

    /// <summary>Назначения по минутам за последние полчаса — для живого графика на обзоре.</summary>
    public async Task<IReadOnlyList<LivePoint>> LiveAsync(int departmentId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lastMinute = FloorDiv(now.ToUnixTimeSeconds(), 60);
        var firstMinute = lastMinute - LiveMinutes + 1;
        var from = DateTimeOffset.FromUnixTimeSeconds(firstMinute * 60);

        var moments = await db.Assignments.AsNoTracking()
            .Where(a => a.DepartmentId == departmentId && a.CreatedAt >= from)
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
    /// Эталон за один час. Пятиминутки идут по порядку, каждая «доливается» поверх нагрузки, набранной в этот час
    /// к её началу, — так же, как выбирает сам алгоритм. Назначения без выбора стоят в своей пятиминутке (строки
    /// «=id»); то, что записано до появления пятиминуток, делится между ними пропорционально потоку. Записи без
    /// пятиминуток (старые) — один отрезок на весь час, как раньше.
    /// </summary>
    private static Dictionary<long, decimal> FairHour(List<ExecutorHourStat> rows, List<EligibilityHourStat> pools,
        IReadOnlyDictionary<long, decimal> qualification, HashSet<(long ExecutorId, long Hour)> capped)
    {
        // назначения без выбора — неизменная часть нагрузки, эталон выравнивает остальное с их учётом
        var forced = rows.GroupBy(r => r.ExecutorId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AssignedWeight - r.FreeWeight));
        var caps = rows.GroupBy(r => r.ExecutorId)
            .Where(g => capped.Contains((g.Key, g.First().BucketHour)))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AssignedWeight));
        var pinned = pools
            .Select(p => (Pool: p, Executor: ExecutorStats.PinnedExecutor(p.SetKey)))
            .Where(x => x.Executor is not null)
            .ToList();
        var groups = pools.Where(p => ExecutorStats.PinnedExecutor(p.SetKey) is null).ToList();
        var total = groups.Sum(p => p.Weight);
        if (total <= 0)
        {
            return forced;
        }

        // то, что не разложено по пятиминуткам, — пропорционально потоку
        var unplaced = forced.ToDictionary(f => f.Key,
            f => Math.Max(0, f.Value - pinned.Where(x => x.Executor == f.Key).Sum(x => x.Pool.Weight)));
        var load = new Dictionary<long, decimal>();
        var added = new Dictionary<long, decimal>();
        foreach (var slot in groups.Select(p => p.Slot).Concat(pinned.Select(x => x.Pool.Slot)).Distinct().Order())
        {
            var slotGroups = groups.Where(p => p.Slot == slot).ToList();
            var share = slotGroups.Sum(p => p.Weight) / total;
            foreach (var (id, weight) in unplaced)
            {
                load[id] = load.GetValueOrDefault(id) + weight * share;
                added[id] = added.GetValueOrDefault(id) + weight * share;
            }

            foreach (var (pool, id) in pinned.Where(x => x.Pool.Slot == slot))
            {
                load[id!.Value] = load.GetValueOrDefault(id.Value) + pool.Weight;
                added[id.Value] = added.GetValueOrDefault(id.Value) + pool.Weight;
            }

            // потолок — полный вес за час: оставляем место под назначения без выбора, которые придут позже
            var slotCaps = caps.ToDictionary(c => c.Key,
                c => c.Value - Math.Max(0, forced.GetValueOrDefault(c.Key) - added.GetValueOrDefault(c.Key)));
            load = FairShare.Allocate(
                slotGroups.GroupBy(p => p.SetKey).Select(g => new FairPool(ExecutorStats.ParseSetKey(g.Key), g.Sum(p => p.Weight))),
                qualification, load, slotCaps);
        }

        return load;
    }

    /// <summary>
    /// Часы, в которых исполнитель набрал суточный лимит: там он больше получить не мог.
    /// Сутки — по местному времени, как у счётчика лимита.
    /// </summary>
    private static HashSet<(long ExecutorId, long Hour)> LimitReached(List<ExecutorHourStat> rows,
        BalancerSnapshot snapshot, long offset)
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
                total += row.AssignedCount - row.SecondaryCount; // как суточный счётчик: без возвратов с доработки
                if (total >= limit)
                {
                    result.Add((row.ExecutorId, row.BucketHour));
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
        var closedWeight = rows.Sum(r => r.ClosedWeight);
        var points = rows.Sum(r => r.Points);
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
            returnRate,
            closedWeight,
            points,
            Motivation.QualityFrom(closed, closedWeight, points),
            rows.Sum(r => r.FastClosedCount),
            rows.Sum(r => r.ExtraCount));
    }

    /// <summary>
    /// Место в рейтинге — по баллам, но только при качестве не ниже порога отдела: иначе быстрый и небрежный
    /// сотрудник набрал бы больше всех просто за счёт количества. Качество ещё не оценено — участвует.
    /// </summary>
    private static List<ExecutorMetrics> RankByPoints(List<ExecutorMetrics> executors, Motivation motivation)
    {
        var rank = 0;
        var ranked = executors
            .Where(e => e.Closed > 0 && (e.Quality is null || e.Quality >= motivation.QualityThreshold))
            .OrderByDescending(e => e.Points).ThenByDescending(e => e.Quality ?? 0).ThenBy(e => e.Id)
            .ToDictionary(e => e.Id, _ => ++rank);
        return executors.Select(e => e with { Rank = ranked.TryGetValue(e.Id, out var r) ? r : null }).ToList();
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
