using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Application.Insights;

/// <param name="WaitMedianSeconds">Сколько заявка ждёт исполнителя: медиана по назначенным без доработки.</param>
public sealed record DemandTotals(
    int Received,
    int Assignments,
    int Closed,
    int Returned,
    int WaitingNow,
    double? WaitMedianSeconds,
    double? WaitP90Seconds,
    int ActiveExecutors);

/// <param name="Executors">Сколько активных сотрудников могут взять заявку с таким значением (по правилам на это поле).</param>
/// <param name="CapacitySharePercent">Их доля в общей квалификации активных сотрудников.</param>
/// <param name="Tension">Доля в заявках ÷ доля в квалификации: 1 — спрос и силы совпадают, 2 — заявок вдвое больше, чем сил.</param>
/// <param name="Label">Как показать значение: подпись справочника (ORDER_3 → «Претензия») или само значение.</param>
public sealed record DemandOption(
    string Value,
    int Orders,
    decimal SharePercent,
    int Waiting,
    int Reworked,
    int Executors,
    decimal CapacitySharePercent,
    decimal? Tension,
    string? Label = null);

public sealed record DemandField(string Key, string Label, IReadOnlyList<DemandOption> Options);

/// <summary>Правило, по которому часть заявок не может взять ни один активный сотрудник.</summary>
public sealed record RuleBlock(string Rule, int Orders, decimal SharePercent);

public sealed record WaitingReason(string Reason, int Orders);

/// <param name="Sampled">По скольким последним заявкам посчитаны разрезы (не больше <see cref="DemandAnalyzer.MaxSample"/>).</param>
/// <param name="Checked">Сколько из них проверено на «никто не может взять» (не больше тысячи).</param>
/// <param name="Unmatched">Проверенные заявки, которым сейчас не подходит ни один активный сотрудник.</param>
public sealed record DemandReport(
    DateTimeOffset From,
    DateTimeOffset To,
    int Sampled,
    DemandTotals Totals,
    IReadOnlyList<DemandField> Fields,
    IReadOnlyList<RuleBlock> Blocked,
    int Checked,
    int Unmatched,
    IReadOnlyList<WaitingReason> Waiting);

/// <summary>
/// Спрос и покрытие за последние сутки: какие заявки приходят, сколько сотрудников их умеют брать, где очередь
/// и доработки, какие правила отсекают заявки от всех. Считается без ИИ — это факты, на которые опирается ИИ-разбор.
/// </summary>
public sealed class DemandAnalyzer(IBalancerDbContext db, ExecutorDirectory directory, TimeProvider clock)
{
    public const int MaxSample = 5000;
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Сколько заявок выборки проверять на «никто не может взять» — проверка идёт по всем сотрудникам.</summary>
    private const int MaxMatchSample = 1000;

    private const int MaxFields = 12;
    private const int MaxOptions = 40;

    public async Task<DemandReport> BuildAsync(int departmentId, CancellationToken cancellationToken)
    {
        var to = clock.GetUtcNow();
        var from = to - Window;
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);

        var sample = await db.Orders.AsNoTracking()
            .Where(o => o.DepartmentId == departmentId && o.ReceivedAt >= from)
            .OrderByDescending(o => o.ReceivedAt)
            .Take(MaxSample)
            .Select(o => new { o.Status, o.ExecutorId, o.AttributesJson, o.ReceivedAt, o.AssignedAt, o.ReworkCount })
            .ToListAsync(cancellationToken);
        var orders = sample
            .Select(o => new Sampled(
                snapshot.Catalog.ParseStored(FieldOwner.Order, o.AttributesJson),
                o.Status == OrderStatus.Processed && o.ExecutorId is null,
                o.ReworkCount > 0))
            .ToList();

        var totals = new DemandTotals(
            Received: await db.Orders.CountAsync(o => o.DepartmentId == departmentId && o.ReceivedAt >= from, cancellationToken),
            Assignments: await db.Assignments.CountAsync(a => a.DepartmentId == departmentId && a.CreatedAt >= from, cancellationToken),
            Closed: await db.Orders.CountAsync(o => o.DepartmentId == departmentId && o.ClosedAt >= from, cancellationToken),
            Returned: await db.OrderStatusChanges.CountAsync(
                c => c.DepartmentId == departmentId && c.To == OrderStatus.Await && c.At >= from, cancellationToken),
            WaitingNow: await db.Orders.CountAsync(
                o => o.DepartmentId == departmentId && o.Status == OrderStatus.Processed && o.ExecutorId == null, cancellationToken),
            WaitMedianSeconds: null,
            WaitP90Seconds: null,
            ActiveExecutors: snapshot.Executors.Values.Count(e => e.IsActive));

        // ожидание — только у заявок без доработки: у вернувшихся время назначения сдвигается
        var waits = sample
            .Where(o => o.AssignedAt is not null && o.ReworkCount == 0)
            .Select(o => Math.Max(0, (o.AssignedAt!.Value - o.ReceivedAt).TotalSeconds))
            .Order()
            .ToList();
        totals = totals with { WaitMedianSeconds = Percentile(waits, 0.5), WaitP90Seconds = Percentile(waits, 0.9) };

        var waiting = await db.Orders.AsNoTracking()
            .Where(o => o.DepartmentId == departmentId && o.Status == OrderStatus.Processed && o.ExecutorId == null)
            .GroupBy(o => o.PendingReason)
            .Select(g => new { Reason = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var active = snapshot.Executors.Values.Where(e => e.IsActive).ToList();
        var (blocked, unmatched) = Blocks(snapshot, active, orders);
        return new DemandReport(from, to, orders.Count, totals, Fields(snapshot, active, orders), blocked,
            Math.Min(orders.Count, MaxMatchSample), unmatched,
            waiting.OrderByDescending(w => w.Count)
                .Select(w => new WaitingReason(w.Reason ?? "без причины", w.Count))
                .ToList());
    }

    private sealed record Sampled(IReadOnlyDictionary<string, FieldValue> Values, bool Waiting, bool Reworked);

    /// <summary>Разрезы по полям-справочникам заявки: спрос по каждому значению против сил, способных его взять.</summary>
    private static List<DemandField> Fields(BalancerSnapshot snapshot, List<ExecutorProfile> active, List<Sampled> orders)
    {
        var totalQualification = active.Sum(e => e.QualificationWeight);
        var result = new List<DemandField>();
        foreach (var field in snapshot.Catalog.All
                     .Where(f => f.Owner == FieldOwner.Order && f.Type == FieldType.Enum && f.Options.Length > 0)
                     .OrderBy(f => f.Id)
                     .Take(MaxFields))
        {
            var rules = snapshot.Rules.Where(r => r.OrderField.Key == field.Key).ToList();
            var options = new List<DemandOption>();
            foreach (var value in field.Options.Take(MaxOptions))
            {
                var matching = orders.Where(o => o.Values.TryGetValue(field.Key, out var v) && v.Key == value).ToList();
                // кто может взять заявку с таким значением — по правилам именно на это поле
                var probe = new Dictionary<string, FieldValue> { [field.Key] = FieldValue.FromText(value, FieldType.Enum) };
                var capable = active.Where(e => rules.All(r => r.Check(probe, e.Values).Passed)).ToList();
                var share = Percent(matching.Count, orders.Count);
                var capacity = totalQualification > 0 ? Math.Round(capable.Sum(e => e.QualificationWeight) * 100m / totalQualification, 1) : 0m;
                options.Add(new DemandOption(value, matching.Count, share, matching.Count(o => o.Waiting),
                    matching.Count(o => o.Reworked), capable.Count, capacity,
                    capacity > 0 && matching.Count > 0 ? Math.Round(share / capacity, 2) : null, field.Show(value)));
            }

            result.Add(new DemandField(field.Key, field.Label, options));
        }

        return result;
    }

    /// <summary>Какие правила отсекают заявки от всех активных сотрудников и сколько заявок не подходит никому.</summary>
    private static (List<RuleBlock> Blocked, int Unmatched) Blocks(BalancerSnapshot snapshot, List<ExecutorProfile> active,
        List<Sampled> orders)
    {
        var checkedOrders = orders.Take(MaxMatchSample).ToList();
        var blocked = new Dictionary<string, int>();
        var unmatched = 0;
        foreach (var order in checkedOrders)
        {
            if (!active.Any(e => snapshot.FirstFailure(order.Values, e) is null))
            {
                unmatched++;
            }

            foreach (var rule in snapshot.Rules)
            {
                if (!active.Any(e => rule.Check(order.Values, e.Values).Passed))
                {
                    blocked[rule.Name] = blocked.GetValueOrDefault(rule.Name) + 1;
                }
            }
        }

        return (blocked.OrderByDescending(b => b.Value)
            .Select(b => new RuleBlock(b.Key, b.Value, Percent(b.Value, checkedOrders.Count)))
            .ToList(), unmatched);
    }

    private static decimal Percent(int part, int total) => total == 0 ? 0 : Math.Round(part * 100m / total, 1);

    private static double? Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? null : Math.Round(sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))], 1);
}
