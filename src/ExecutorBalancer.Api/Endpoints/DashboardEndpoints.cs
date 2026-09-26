using System.Globalization;
using System.Text;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Api.Endpoints;

/// <summary>Данные для дашборда. Только чтение, только для вошедшего администратора.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Дашборд").RequireAuthorization();
        group.MapGet("/summary", Summary);
        group.MapGet("/feed", Feed);
        group.MapGet("/orders/{id:long}", OrderDetails);
        group.MapGet("/live", (AnalyticsService analytics, CancellationToken ct) => analytics.LiveAsync(ct));
        group.MapGet("/analytics", Analytics);
        group.MapGet("/export.csv", Export);
        return app;
    }

    /// <summary>Периоды отчёта в запросе: today, 24h, 7d, 30d.</summary>
    public static bool TryParsePeriod(string? value, out AnalyticsPeriod period)
    {
        (var ok, period) = value switch
        {
            null or "" or "today" => (true, AnalyticsPeriod.Today),
            "24h" => (true, AnalyticsPeriod.Day),
            "7d" => (true, AnalyticsPeriod.Week),
            "30d" => (true, AnalyticsPeriod.Month),
            _ => (false, AnalyticsPeriod.Today),
        };
        return ok;
    }

    public static IResult BadPeriod() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["period"] = ["допустимо: today, 24h, 7d, 30d"],
    });

    private static async Task<IResult> Analytics(string? period, AnalyticsService analytics, CancellationToken ct) =>
        TryParsePeriod(period, out var parsed) ? Results.Ok(await analytics.BuildAsync(parsed, ct)) : BadPeriod();

    /// <summary>
    /// Выгрузка для Excel: CSV в UTF-8 с BOM и разделителем «;» — так файл открывается в русском Excel
    /// двойным щелчком. Текстовые ячейки, начинающиеся с = + - @, экранируются: имя исполнителя приходит
    /// из внешней системы и не должно исполниться как формула.
    /// </summary>
    private static async Task<IResult> Export(string? period, AnalyticsService analytics,
        IOptions<BalancerOptions> options, CancellationToken ct)
    {
        if (!TryParsePeriod(period, out var parsed))
        {
            return BadPeriod();
        }

        var report = await analytics.BuildAsync(parsed, ct);
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var csv = new StringBuilder();
        csv.AppendLine(Row("Отчёт за период", $"{Local(report.From)} — {Local(report.To)} ({options.Value.TimeZone})"));
        csv.AppendLine(Row("Среднее отклонение от справедливой доли, %", Number(report.Fairness.MeanAbsDeviationPercent)));
        csv.AppendLine(Row("Максимальное отклонение, %", Number(report.Fairness.MaxAbsDeviationPercent)));
        csv.AppendLine();
        csv.AppendLine(Row("ID", "Исполнитель", "Активен", "Квалификация", "Назначено", "Вес назначенных",
            "Первичные", "Перераспределения", "От родителя", "Вторичные", "Вес свободного выбора",
            "Справедливый вес", "Отклонение, %", "Решено и отклонено", "На доработку", "Доля возвратов, %"));
        foreach (var e in report.Executors)
        {
            csv.AppendLine(Row(Number(e.Id), Text(e.Name), e.IsActive ? "да" : "нет", Number(e.Qualification),
                Number(e.Assigned), Number(e.AssignedWeight), Number(e.Primary), Number(e.Reassign), Number(e.Parent),
                Number(e.Secondary), Number(e.FreeWeight), Number(e.FairWeight), Number(e.DeviationPercent),
                Number(e.Closed), Number(e.Returned), Number(e.ReturnRatePercent)));
        }

        csv.AppendLine();
        csv.AppendLine(Row("Начало интервала", "Назначено", "Вес", "Решено и отклонено", "На доработку"));
        foreach (var point in report.Timeline)
        {
            csv.AppendLine(Row(Local(point.Start), Number(point.Assigned),
                Number(point.AssignedWeight), Number(point.Closed), Number(point.Returned)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"executor-balancer-{period ?? "today"}.csv");

        string Number(decimal? value) => value?.ToString(culture) ?? "";
        string Local(DateTimeOffset moment) =>
            TimeZoneInfo.ConvertTime(moment, zone).ToString("dd.MM.yyyy HH:mm", culture);
        static string Row(params string[] cells) => string.Join(';', cells.Select(Quote));
        static string Text(string value) => value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        static string Quote(string cell) =>
            cell.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }

    private static async Task<IResult> Summary(IBalancerDbContext db, ExecutorDirectory directory, ILoadStore loads,
        OrderBalancer balancer, AnalyticsService analytics, CancellationToken ct)
    {
        var today = await analytics.BuildAsync(AnalyticsPeriod.Today, ct);
        var deviations = today.Executors.ToDictionary(e => e.Id, e => e.DeviationPercent);
        var snapshot = await directory.GetAsync(ct);
        var current = await loads.GetLoadsAsync(balancer.Today(), ct);
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);

        var pending = await db.Orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId == null, ct);
        var open = await db.Orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId != null, ct);
        var lastHour = await db.Assignments.CountAsync(a => a.CreatedAt >= hourAgo, ct);
        var total = await db.Orders.CountAsync(ct);
        var undelivered = await db.OutboxMessages.CountAsync(m => m.SentAt == null, ct);

        var executors = snapshot.Executors.Values
            .OrderBy(e => e.Id)
            .Select(e =>
            {
                var load = current.GetValueOrDefault(e.Id) ?? new ExecutorLoad(0, 0, 0);
                var openWeight = load.OpenWeightMilli / 1000m;
                return new
                {
                    e.Id,
                    e.FullName,
                    e.IsActive,
                    e.DailyLimit,
                    e.QualificationWeight,
                    OpenCount = load.OpenCount,
                    OpenWeight = openWeight,
                    load.AssignedToday,
                    // нагрузка на единицу квалификации — именно её выравнивает алгоритм
                    RelativeLoad = Math.Round(openWeight / e.QualificationWeight, 3),
                    DeviationPercent = deviations.GetValueOrDefault(e.Id),
                    // что умеет исполнитель: параметры из справочника с человеческими названиями
                    Skills = snapshot.Catalog.All
                        .Where(f => f.Owner == FieldOwner.Executor && e.Values.ContainsKey(f.Key))
                        .OrderBy(f => f.Id)
                        .Select(f => new { f.Key, f.Label, Value = Display(e.Values[f.Key]) })
                        .ToList(),
                };
            })
            .ToList();

        return Results.Ok(new
        {
            Totals = new
            {
                Orders = total,
                Open = open,
                Pending = pending,
                AssignedLastHour = lastHour,
                Undelivered = undelivered,
                ActiveExecutors = executors.Count(e => e.IsActive),
                AssignedToday = today.Timeline.Sum(p => p.Assigned),
                ClosedToday = today.Timeline.Sum(p => p.Closed),
            },
            Fairness = today.Fairness,
            RuleErrors = snapshot.RuleErrors,
            Executors = executors,
        });
    }

    private static async Task<IResult> Feed(long? after, IBalancerDbContext db, ExecutorDirectory directory,
        CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(ct);
        var query = db.Assignments.AsNoTracking();
        if (after is { } afterId)
        {
            query = query.Where(a => a.Id > afterId);
        }

        var rows = await query.OrderByDescending(a => a.Id).Take(50)
            .Select(a => new { a.Id, a.OrderId, a.ExecutorId, a.Kind, a.Score, a.OrderWeight, a.CreatedAt })
            .ToListAsync(ct);
        var orderIds = rows.Select(r => r.OrderId).Distinct().ToList();
        var attributes = await db.Orders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.AttributesJson })
            .ToDictionaryAsync(o => o.Id, o => o.AttributesJson, ct);

        return Results.Ok(rows.Select(a => new
        {
            a.Id,
            a.OrderId,
            a.ExecutorId,
            ExecutorName = snapshot.Executors.TryGetValue(a.ExecutorId, out var e) ? e.FullName : $"#{a.ExecutorId}",
            Kind = a.Kind.ToString(),
            a.Score,
            a.OrderWeight,
            a.CreatedAt,
            Summary = OrderSummary(snapshot, attributes.GetValueOrDefault(a.OrderId)),
        }));
    }

    private static string Display(FieldValue value) => value.IsArray
        ? string.Join(", ", value.Items)
        : value.Type == FieldType.Number
            ? value.Number.ToString("#,0.##", CultureInfo.GetCultureInfo("ru-RU"))
            : value.Type == FieldType.Boolean ? (value.Flag ? "да" : "нет") : value.Text;

    /// <summary>Коротко, что за заявка: первые три параметра из справочника в порядке их заведения.</summary>
    private static string OrderSummary(BalancerSnapshot snapshot, string? json)
    {
        if (json is null)
        {
            return "";
        }

        var values = snapshot.Catalog.ParseStored(FieldOwner.Order, json);
        return string.Join(" · ", snapshot.Catalog.All
            .Where(f => f.Owner == FieldOwner.Order && values.ContainsKey(f.Key))
            .OrderBy(f => f.Id)
            .Take(3)
            .Select(f => Display(values[f.Key])));
    }

    private static async Task<IResult> OrderDetails(long id, IBalancerDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Заявка не найдена");
        }

        var history = await db.Assignments.AsNoTracking()
            .Where(a => a.OrderId == id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            order.Id,
            order.ParentId,
            Status = order.Status.ToString(),
            order.Weight,
            order.ExecutorId,
            order.PendingReason,
            order.ReceivedAt,
            Attributes = System.Text.Json.JsonDocument.Parse(order.AttributesJson).RootElement,
            History = history.Select(a => new
            {
                a.Id,
                a.ExecutorId,
                Kind = a.Kind.ToString(),
                a.Score,
                a.IsCurrent,
                a.CreatedAt,
                Explanation = AssignmentExplanation.FromJson(a.ExplanationJson),
            }),
        });
    }
}
