using System.Globalization;
using System.Text;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
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
        var group = app.MapGroup("/api/dashboard").WithTags("Дашборд").RequireAuthorization()
            .AddEndpointFilter(DepartmentScope.RequireDepartment);
        group.MapGet("/summary", Summary);
        group.MapGet("/feed", Feed);
        group.MapGet("/orders", OrderList);
        group.MapGet("/orders/{id:long}", OrderDetails);
        group.MapGet("/executors/{id:long}", ExecutorPage);
        group.MapGet("/live", (DepartmentScope d, AnalyticsService analytics, CancellationToken ct) => analytics.LiveAsync(d.Id, ct));
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

    private static async Task<IResult> Analytics(DepartmentScope d, string? period, AnalyticsService analytics, CancellationToken ct) =>
        TryParsePeriod(period, out var parsed) ? Results.Ok(await analytics.BuildAsync(d.Id, parsed, ct)) : BadPeriod();

    /// <summary>
    /// Выгрузка для Excel: CSV в UTF-8 с BOM и разделителем «;» — так файл открывается в русском Excel
    /// двойным щелчком. Текстовые ячейки, начинающиеся с = + - @, экранируются: имя исполнителя приходит
    /// из внешней системы и не должно исполниться как формула.
    /// </summary>
    private static async Task<IResult> Export(DepartmentScope d, string? period, AnalyticsService analytics,
        IOptions<BalancerOptions> options, CancellationToken ct)
    {
        if (!TryParsePeriod(period, out var parsed))
        {
            return BadPeriod();
        }

        var report = await analytics.BuildAsync(d.Id, parsed, ct);
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var csv = new StringBuilder();
        csv.AppendLine(Row("Отчёт за период", $"{Local(report.From)} — {Local(report.To)} ({options.Value.TimeZone})"));
        csv.AppendLine(Row("Среднее отклонение от справедливой доли, %", Number(report.Fairness.MeanAbsDeviationPercent)));
        csv.AppendLine(Row("Максимальное отклонение, %", Number(report.Fairness.MaxAbsDeviationPercent)));
        csv.AppendLine();
        csv.AppendLine(Row("ID", "Исполнитель", "Активен", "Квалификация", "Назначено", "Вес назначенных",
            "Первичные", "Перераспределения", "От родителя", "Вторичные", "Вес свободного выбора",
            "Справедливый вес", "Отклонение, %", "Решено и отклонено", "На доработку", "Доля возвратов, %",
            "Место в рейтинге", "Баллы рейтинга", "Качество, %", "Закрыто подозрительно быстро", "Сверх нормы"));
        foreach (var e in report.Executors)
        {
            csv.AppendLine(Row(Number(e.Id), Text(e.Name), e.IsActive ? "да" : "нет", Number(e.Qualification),
                Number(e.Assigned), Number(e.AssignedWeight), Number(e.Primary), Number(e.Reassign), Number(e.Parent),
                Number(e.Secondary), Number(e.FreeWeight), Number(e.FairWeight), Number(e.DeviationPercent),
                Number(e.Closed), Number(e.Returned), Number(e.ReturnRatePercent),
                e.Rank is { } rank ? Number(rank) : "вне рейтинга", Number(e.Points), Number(e.Quality is { } q ? Math.Round(q * 100m, 1) : null), Number(e.FastClosed),
                Number(e.Extra)));
        }

        csv.AppendLine();
        csv.AppendLine(Row("Начало интервала", "Назначено", "Вес", "Решено и отклонено", "На доработку"));
        foreach (var point in report.Timeline)
        {
            csv.AppendLine(Row(Local(point.Start), Number(point.Assigned),
                Number(point.AssignedWeight), Number(point.Closed), Number(point.Returned)));
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"executor-balancer-{d.Id}-{period ?? "today"}.csv");

        string Number(decimal? value) => value?.ToString(culture) ?? "";
        string Local(DateTimeOffset moment) =>
            TimeZoneInfo.ConvertTime(moment, zone).ToString("dd.MM.yyyy HH:mm", culture);
        static string Row(params string[] cells) => string.Join(';', cells.Select(Quote));
        static string Text(string value) => value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        static string Quote(string cell) =>
            cell.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }

    private static async Task<IResult> Summary(DepartmentScope d, IBalancerDbContext db, ExecutorDirectory directory, ILoadStore loads,
        OrderBalancer balancer, AnalyticsService analytics, QualityTracker quality, CancellationToken ct)
    {
        var today = await analytics.BuildAsync(d.Id, AnalyticsPeriod.Today, ct);
        var deviations = today.Executors.ToDictionary(e => e.Id, e => e.DeviationPercent);
        var snapshot = await directory.GetAsync(d.Id, ct);
        var current = await loads.GetLoadsAsync(balancer.Today(), balancer.CurrentHour(), ct);
        var scores = await quality.GetAsync(d.Id, ct);
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);

        var orders = db.Orders.Where(o => o.DepartmentId == d.Id);
        var pending = await orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId == null, ct);
        var open = await orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId != null, ct);
        var lastHour = await db.Assignments.CountAsync(a => a.DepartmentId == d.Id && a.CreatedAt >= hourAgo, ct);
        var total = await orders.CountAsync(ct);
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
                    // режим «больше нормы» и качество за последние дни (защита от работы на количество)
                    e.ExtraPercent,
                    Extra = snapshot.Motivation.Extra(e, scores.GetValueOrDefault(e.Id), 0m),
                    Quality = scores.GetValueOrDefault(e.Id),
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

    private static async Task<IResult> Feed(DepartmentScope d, long? after, IBalancerDbContext db, ExecutorDirectory directory,
        CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(d.Id, ct);
        var query = db.Assignments.AsNoTracking().Where(a => a.DepartmentId == d.Id);
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

    /// <summary>
    /// Страница сотрудника: что умеет (параметры отдела и в каких правилах они участвуют), сегодняшняя норма,
    /// качество и баллы, место в рейтинге за 7 дней, активность по дням и последние заявки.
    /// </summary>
    private static async Task<IResult> ExecutorPage(DepartmentScope d, long id, IBalancerDbContext db,
        ExecutorDirectory directory, ILoadStore loads, OrderBalancer balancer, AnalyticsService analytics,
        QualityTracker quality, IOptions<BalancerOptions> options, CancellationToken ct)
    {
        var snapshot = await directory.GetAsync(d.Id, ct);
        if (!snapshot.Executors.TryGetValue(id, out var e))
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Сотрудник не найден в этом отделе");
        }

        var department = await db.Departments.AsNoTracking().Where(x => x.Id == d.Id)
            .Select(x => new { x.Name, x.PresetId }).FirstAsync(ct);
        var updatedAt = await db.Executors.AsNoTracking().Where(x => x.Id == id).Select(x => x.UpdatedAt).FirstAsync(ct);
        var load = (await loads.GetLoadsAsync(balancer.Today(), balancer.CurrentHour(), ct)).GetValueOrDefault(id) ?? new ExecutorLoad(0, 0, 0);
        var scores = await quality.GetAsync(d.Id, ct);
        var week = await analytics.BuildAsync(d.Id, AnalyticsPeriod.Week, ct);
        var weekMetrics = week.Executors.FirstOrDefault(m => m.Id == id);

        // что умеет: все параметры сотрудника из справочника отдела, с правилами, где они участвуют
        var rules = snapshot.Rules;
        var skills = snapshot.Catalog.All
            .Where(f => f.Owner == FieldOwner.Executor)
            .OrderBy(f => f.Id)
            .Select(f => new
            {
                f.Key,
                f.Label,
                Type = f.Type.ToString(),
                Values = e.Values.TryGetValue(f.Key, out var v)
                    ? (v.IsArray ? v.Items.ToArray() : [Display(v)])
                    : [],
                Rules = rules.Where(r => r.ExecutorField?.Key == f.Key || r.ExecutorFieldUpper?.Key == f.Key)
                    .Select(r => r.Name).Distinct().ToArray(),
            })
            .ToList();

        // активность по дням за неделю — по местному времени
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        var today = balancer.Today();
        var fromDay = today.AddDays(-6);
        var fromUtc = new DateTimeOffset(fromDay.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(DateTime.UtcNow)).ToUniversalTime();
        var fromBucket = ExecutorStats.HourOf(fromUtc);
        var hours = await db.ExecutorHourStats.AsNoTracking()
            .Where(s => s.ExecutorId == id && s.DepartmentId == d.Id && s.BucketHour >= fromBucket)
            .ToListAsync(ct);
        var days = Enumerable.Range(0, 7).Select(i => fromDay.AddDays(i)).Select(day =>
        {
            var rows = hours.Where(h =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ExecutorStats.HourStart(h.BucketHour), zone).DateTime) == day).ToList();
            return new
            {
                Day = day,
                Assigned = rows.Sum(r => r.AssignedCount),
                Closed = rows.Sum(r => r.ClosedCount),
                Points = rows.Sum(r => r.Points),
            };
        }).ToList();

        var recent = await db.Assignments.AsNoTracking()
            .Where(a => a.ExecutorId == id && a.DepartmentId == d.Id)
            .OrderByDescending(a => a.Id).Take(15)
            .Select(a => new { a.OrderId, a.Kind, a.OrderWeight, a.CreatedAt, a.IsCurrent })
            .ToListAsync(ct);
        var orderIds = recent.Select(r => r.OrderId).Distinct().ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Status, o.Points, o.AttributesJson, o.ExecutorId })
            .ToDictionaryAsync(o => o.Id, ct);

        return Results.Ok(new
        {
            e.Id,
            e.FullName,
            e.IsActive,
            Department = department.Name,
            Sphere = DomainPresets.Find(department.PresetId)?.Title,
            e.QualificationWeight,
            e.DailyLimit,
            e.ExtraPercent,
            Extra = snapshot.Motivation.Extra(e, scores.GetValueOrDefault(id), 0m),
            UpdatedAt = updatedAt,
            Today = new { load.OpenCount, OpenWeight = load.OpenWeightMilli / 1000m, load.AssignedToday },
            Quality = scores.GetValueOrDefault(id),
            QualityThreshold = snapshot.Motivation.QualityThreshold,
            Week = weekMetrics is null ? null : new
            {
                weekMetrics.Assigned, weekMetrics.Closed, weekMetrics.Returned, weekMetrics.Points, weekMetrics.Quality,
                weekMetrics.FastClosed, weekMetrics.Extra, weekMetrics.Rank, weekMetrics.DeviationPercent,
                Rated = week.Executors.Count(m => m.Rank is not null),
            },
            Skills = skills,
            Days = days,
            Recent = recent.Select(r =>
            {
                var order = orders.GetValueOrDefault(r.OrderId);
                return new
                {
                    r.OrderId,
                    Kind = r.Kind.ToString(),
                    r.OrderWeight,
                    r.CreatedAt,
                    // заявка могла уйти другому — тогда статус не этого сотрудника
                    Status = order is null ? null : order.ExecutorId == id ? order.Status.ToString() : "Moved",
                    Points = order?.ExecutorId == id ? order.Points : null,
                    Summary = OrderSummary(snapshot, order?.AttributesJson),
                };
            }),
        });
    }

    /// <summary>Заявки отдела, новые сверху: фильтр по состоянию и счётчики для вкладок-фильтров.</summary>
    /// <param name="state">all, pending (ждёт сотрудника), processed (в работе), await (на доработке), closed.</param>
    private static async Task<IResult> OrderList(DepartmentScope d, string? state, long? before, int? limit,
        IBalancerDbContext db, ExecutorDirectory directory, CancellationToken ct)
    {
        var orders = db.Orders.AsNoTracking().Where(o => o.DepartmentId == d.Id);
        IQueryable<Order>? filtered = state switch
        {
            null or "" or "all" => orders,
            "pending" => orders.Where(o => o.Status == OrderStatus.Processed && o.ExecutorId == null),
            "processed" => orders.Where(o => o.Status == OrderStatus.Processed && o.ExecutorId != null),
            "await" => orders.Where(o => o.Status == OrderStatus.Await),
            "closed" => orders.Where(o => o.Status == OrderStatus.Accept || o.Status == OrderStatus.Reject),
            _ => null,
        };
        if (filtered is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["state"] = ["допустимо: all, pending, processed, await, closed"],
            });
        }

        if (before is { } beforeId)
        {
            filtered = filtered.Where(o => o.Id < beforeId);
        }

        var rows = await filtered.OrderByDescending(o => o.Id).Take(Math.Clamp(limit ?? 50, 1, 200))
            .Select(o => new
            {
                o.Id, o.Status, o.ExecutorId, o.PendingReason, o.Weight, o.ReceivedAt, o.ClosedAt, o.Points,
                o.AttributesJson, o.ReworkCount,
            })
            .ToListAsync(ct);
        var counts = new
        {
            All = await orders.CountAsync(ct),
            Pending = await orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId == null, ct),
            Processed = await orders.CountAsync(o => o.Status == OrderStatus.Processed && o.ExecutorId != null, ct),
            Await = await orders.CountAsync(o => o.Status == OrderStatus.Await, ct),
            Closed = await orders.CountAsync(o => o.Status == OrderStatus.Accept || o.Status == OrderStatus.Reject, ct),
        };
        var snapshot = await directory.GetAsync(d.Id, ct);
        var names = await ExecutorNamesAsync(db, snapshot, rows.Select(r => r.ExecutorId), ct);
        return Results.Ok(new
        {
            Counts = counts,
            Items = rows.Select(o => new
            {
                o.Id,
                Status = o.Status.ToString(),
                Waiting = o.Status == OrderStatus.Processed && o.ExecutorId is null,
                o.ExecutorId,
                ExecutorName = o.ExecutorId is { } e ? names.GetValueOrDefault(e) : null,
                o.PendingReason,
                o.Weight,
                o.ReceivedAt,
                o.ClosedAt,
                o.Points,
                o.ReworkCount,
                Summary = OrderSummary(snapshot, o.AttributesJson),
            }),
        });
    }

    /// <summary>
    /// Карточка заявки: параметры с названиями и путь от поступления до результата — поступила, кому и почему
    /// назначена, доставлена ли в АИС, доработки, решение и балл. Каждое назначение — с полным объяснением.
    /// </summary>
    private static async Task<IResult> OrderDetails(DepartmentScope d, long id, IBalancerDbContext db,
        ExecutorDirectory directory, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id && o.DepartmentId == d.Id, ct);
        if (order is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Заявка не найдена");
        }

        var history = await db.Assignments.AsNoTracking().Where(a => a.OrderId == id).OrderBy(a => a.Id).ToListAsync(ct);
        var deliveries = await db.OutboxMessages.AsNoTracking().Where(m => m.OrderId == id).OrderBy(m => m.Id).ToListAsync(ct);
        var changes = await db.OrderStatusChanges.AsNoTracking().Where(c => c.OrderId == id).OrderBy(c => c.Id).ToListAsync(ct);
        var snapshot = await directory.GetAsync(d.Id, ct);
        var names = await ExecutorNamesAsync(db, snapshot,
            history.Select(a => (long?)a.ExecutorId).Concat(deliveries.Select(m => (long?)m.ExecutorId)), ct);
        string Name(long? executor) => executor is { } e ? names.GetValueOrDefault(e) ?? $"#{e}" : "—";

        var values = snapshot.Catalog.ParseStored(FieldOwner.Order, order.AttributesJson);
        var parameters = snapshot.Catalog.All
            .Where(f => f.Owner == FieldOwner.Order && values.ContainsKey(f.Key))
            .OrderBy(f => f.Id)
            .Select(f => new { f.Label, Value = Display(values[f.Key]) })
            .ToList();

        var events = new List<TimelineEvent>
        {
            new(order.ReceivedAt, "received", "Поступила из АИС",
                $"вес {order.Weight.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"))}" +
                (order.ParentId is { } parent ? $" · связана с заявкой #{parent}" : "")),
        };
        foreach (var a in history)
        {
            var explanation = AssignmentExplanation.FromJson(a.ExplanationJson);
            var suitable = explanation?.Candidates.Count(c => c.Verdict is "eligible" or "chosen" or "over_norm") ?? 0;
            var total = explanation?.Candidates.Count ?? 0;
            var title = a.Kind switch
            {
                AssignmentKind.Reassign => "Передана другому сотруднику",
                AssignmentKind.Parent => "Назначена исполнителю родительской заявки",
                AssignmentKind.Secondary => "После доработки — снова тому же сотруднику",
                AssignmentKind.Extra => "Назначена сверх нормы",
                _ => "Назначена сотруднику",
            };
            // от родителя и после доработки — без выбора: заявка идёт «своему» сотруднику
            var choice = a.Kind is AssignmentKind.Parent or AssignmentKind.Secondary
                ? "без выбора — продолжение уже начатой работы"
                : $"могли взять {suitable} из {total}";
            events.Add(new(a.CreatedAt, "assigned", title, $"{Name(a.ExecutorId)} · {choice}", a.Id, a.ExecutorId,
                Name(a.ExecutorId)));
        }

        foreach (var m in deliveries)
        {
            events.Add(m.SentAt is { } sent
                ? new(sent, "delivered", "Назначение доставлено в АИС", $"{Name(m.ExecutorId)} появится у сотрудника в АИС")
                : new(m.CreatedAt, "delivering", "Доставляется в АИС",
                    m.Attempts > 0 ? $"попыток: {m.Attempts}, повтор с растущей паузой" : "в очереди на отправку"));
        }

        foreach (var c in changes)
        {
            var (kind, title) = c.To switch
            {
                OrderStatus.Await => ("await", "Отправлена на доработку"),
                OrderStatus.Accept => ("closed", "Решена"),
                OrderStatus.Reject => ("closed", "Отклонена"),
                _ => ("returned", c.From == OrderStatus.Await ? "Вернулась с доработки" : "Открыта заново"),
            };
            var text = c.ExecutorId is { } who ? $"у сотрудника {Name(who)}" : "";
            if (kind == "closed" && order.Points is { } points && c == changes.Last())
            {
                text += $" · баллов в рейтинг: {points.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"))} (вес × качество)";
            }

            events.Add(new(c.At, kind, title, text.TrimStart(' ', '·')));
        }

        if (order.Status == OrderStatus.Processed && order.ExecutorId is null)
        {
            events.Add(new(DateTimeOffset.UtcNow, "waiting", "Ждёт подходящего сотрудника",
                $"{order.PendingReason ?? "ожидает распределения"} — повтор каждые 5 секунд"));
        }

        return Results.Ok(new
        {
            order.Id,
            order.ParentId,
            Status = order.Status.ToString(),
            order.Weight,
            order.ExecutorId,
            ExecutorName = order.ExecutorId is { } current ? Name(current) : null,
            order.PendingReason,
            order.ReceivedAt,
            order.Points,
            order.ReworkCount,
            Summary = OrderSummary(snapshot, order.AttributesJson),
            Parameters = parameters,
            Attributes = System.Text.Json.JsonDocument.Parse(order.AttributesJson).RootElement,
            // порядок по времени; при равном — в порядке добавления (поступила → назначена → доставлена)
            Timeline = events.Select((e, i) => (e, i)).OrderBy(x => x.e.At).ThenBy(x => x.i).Select(x => x.e),
            History = history.Select(a => new
            {
                a.Id,
                a.ExecutorId,
                ExecutorName = Name(a.ExecutorId),
                Kind = a.Kind.ToString(),
                a.Score,
                a.IsCurrent,
                a.CreatedAt,
                Explanation = AssignmentExplanation.FromJson(a.ExplanationJson),
            }),
        });
    }

    /// <summary>Шаг пути заявки. AssignmentId — к какому назначению относится объяснение.</summary>
    private sealed record TimelineEvent(DateTimeOffset At, string Kind, string Title, string Text,
        long? AssignmentId = null, long? ExecutorId = null, string? ExecutorName = null);

    /// <summary>Имена сотрудников: из снимка отдела, ушедших в другой отдел — из базы.</summary>
    private static async Task<Dictionary<long, string>> ExecutorNamesAsync(IBalancerDbContext db, BalancerSnapshot snapshot,
        IEnumerable<long?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<long>().Distinct().ToList();
        var names = wanted.Where(snapshot.Executors.ContainsKey).ToDictionary(e => e, e => snapshot.Executors[e].FullName);
        var missing = wanted.Where(e => !names.ContainsKey(e)).ToList();
        if (missing.Count > 0)
        {
            foreach (var e in await db.Executors.AsNoTracking().Where(x => missing.Contains(x.Id))
                         .Select(x => new { x.Id, x.FullName }).ToListAsync(ct))
            {
                names[e.Id] = e.FullName;
            }
        }

        return names;
    }
}
