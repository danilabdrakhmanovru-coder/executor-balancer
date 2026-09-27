using System.Text.Json;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExecutorBalancer.Application.Balancing;

/// <summary>
/// Приём заявок и назначение исполнителя.
/// Порядок: проверка правил в памяти → атомарный выбор и увеличение нагрузки в Redis →
/// сохранение решения и сообщения для АИС в PostgreSQL одной транзакцией.
/// Нагрузку из АИС не читаем: там назначение появляется с задержкой 2–10 секунд.
/// </summary>
public sealed class OrderBalancer(
    IBalancerDbContext db,
    ExecutorDirectory directory,
    ILoadStore loadStore,
    QualityTracker quality,
    IOptions<BalancerOptions> options,
    TimeProvider clock,
    ILogger<OrderBalancer> logger)
{
    private const string WaitingReason = "ожидает распределения";

    /// <summary>
    /// Выбор в Redis и запись в базу занимают миллисекунды. Если заявка числится за исполнителем в Redis
    /// дольше, а в базе исполнителя нет — процесс упал между этими шагами, и выбор надо откатить.
    /// </summary>
    private static readonly TimeSpan OrphanedHoldAge = TimeSpan.FromMinutes(1);

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public async Task<BalanceResult> ReceiveAsync(int departmentId, IncomingOrder incoming,
        CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var values = snapshot.Catalog.Parse(FieldOwner.Order, incoming.Attributes, errors);
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }

        var existing = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == incoming.Id, cancellationToken);
        if (existing is not null)
        {
            return BalanceResult.FromOrder(existing, duplicate: true);
        }

        var order = new Order
        {
            Id = incoming.Id,
            DepartmentId = departmentId,
            ParentId = incoming.ParentId,
            Status = incoming.Status,
            Weight = snapshot.OrderWeight(values),
            AttributesJson = JsonSerializer.Serialize(incoming.Attributes),
            PendingReason = incoming.Status == OrderStatus.Processed ? WaitingReason : null,
            ReceivedAt = clock.GetUtcNow(),
        };
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            // та же заявка пришла параллельно на другой запрос или экземпляр
            db.Detach(order);
            var winner = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == incoming.Id, cancellationToken);
            return BalanceResult.FromOrder(winner, duplicate: true);
        }

        if (order.Status != OrderStatus.Processed)
        {
            return BalanceResult.FromOrder(order);
        }

        return await AssignAsync(order, values, snapshot, previousExecutorId: null, reopen: false, cancellationToken);
    }

    public async Task<BalanceResult?> ChangeStatusAsync(long orderId, OrderStatus status,
        CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return null;
        }

        if (order.Status == status)
        {
            return BalanceResult.FromOrder(order, duplicate: true);
        }

        if (order.Status == OrderStatus.Processed && order.ExecutorId is not null)
        {
            var release = status == OrderStatus.Await ? LoadRelease.Await : LoadRelease.Close;
            await loadStore.ReleaseAsync(order.Id, release, cancellationToken);
        }

        var previousStatus = order.Status;
        var now = clock.GetUtcNow();
        var closed = status is OrderStatus.Accept or OrderStatus.Reject;
        var returned = previousStatus == OrderStatus.Processed && status == OrderStatus.Await;
        order.Status = status;
        order.ClosedAt = closed ? now : null;
        order.PendingReason = status == OrderStatus.Processed ? WaitingReason : null;
        StatDelta? delta = null;
        if (order.ExecutorId is { } owner && (closed || returned))
        {
            if (returned)
            {
                order.ReworkCount++;
                delta = new StatDelta(owner, Returned: 1);
            }
            else
            {
                // балл рейтинга = вес заявки × коэффициент качества (доработки, слишком быстрое закрытие)
                var motivation = (await directory.GetAsync(order.DepartmentId, cancellationToken)).Motivation;
                var factor = motivation.QualityOf(order.ReworkCount, now - order.AssignedAt, out var fast);
                order.Points = Math.Round(order.Weight * factor, 3);
                delta = new StatDelta(owner, Closed: 1, ClosedWeight: order.Weight, Points: order.Points.Value,
                    FastClosed: fast ? 1 : 0);
            }
        }

        db.OrderStatusChanges.Add(new OrderStatusChange
        {
            OrderId = order.Id, DepartmentId = order.DepartmentId, From = previousStatus, To = status,
            ExecutorId = order.ExecutorId, At = now,
        });

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            if (delta is not null)
            {
                await ExecutorStats.RecordAsync(db, now, order.DepartmentId, [delta], cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (status != OrderStatus.Processed)
        {
            return BalanceResult.FromOrder(order);
        }

        logger.LogDebug("Заявка {OrderId} вернулась в рассмотрение из статуса {Status}", order.Id, previousStatus);
        var snapshot = await directory.GetAsync(order.DepartmentId, cancellationToken);
        var values = snapshot.Catalog.ParseStored(FieldOwner.Order, order.AttributesJson);
        return await AssignAsync(order, values, snapshot, order.ExecutorId, reopen: true, cancellationToken);
    }

    /// <summary>Повторная попытка для заявки, которая ждёт исполнителя.</summary>
    public async Task<BalanceResult?> RetryPendingAsync(long orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null || order.Status != OrderStatus.Processed || order.ExecutorId is not null)
        {
            return null;
        }

        var snapshot = await directory.GetAsync(order.DepartmentId, cancellationToken);
        var values = snapshot.Catalog.ParseStored(FieldOwner.Order, order.AttributesJson);
        // reopen: заявка могла раньше принадлежать исполнителю и вернуться в очередь
        return await AssignAsync(order, values, snapshot, previousExecutorId: null, reopen: true, cancellationToken);
    }

    /// <summary>
    /// Сотрудник из АИС. Если он числился в другом отделе — переводится: его открытые заявки там
    /// перераспределяются между коллегами прежнего отдела, как при уходе.
    /// </summary>
    public async Task UpsertExecutorAsync(int departmentId, IncomingExecutor incoming, CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var errors = new Dictionary<string, string[]>();
        snapshot.Catalog.Parse(FieldOwner.Executor, incoming.Attributes, errors);
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }

        var (executor, wasActive, moved) = Apply(departmentId, incoming,
            await db.Executors.FirstOrDefaultAsync(e => e.Id == incoming.Id, cancellationToken),
            await db.ExecutorQualifications.AnyAsync(q => q.ExecutorId == incoming.Id, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);

        await loadStore.SetActiveAsync(executor.Id, executor.IsActive, cancellationToken);
        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();

        if ((wasActive && !executor.IsActive) || moved)
        {
            await ReassignOpenOrdersAsync(executor.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Пакетная загрузка сотрудников отдела (из файла): одна транзакция вместе с записью журнала, одна новая
    /// версия конфигурации. Данные уже проверены по справочнику. Ушедшие с работы и переведённые из других
    /// отделов отдают свои открытые заявки коллегам — как при обычной передаче из АИС.
    /// </summary>
    public async Task ImportExecutorsAsync(int departmentId, IReadOnlyList<IncomingExecutor> incoming, AuditEntry audit,
        CancellationToken cancellationToken)
    {
        var ids = incoming.Select(e => e.Id).ToList();
        var existing = await db.Executors.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var withHistory = (await db.ExecutorQualifications.Where(q => ids.Contains(q.ExecutorId))
            .Select(q => q.ExecutorId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var released = new List<long>();
        var saved = new List<Executor>();
        foreach (var item in incoming)
        {
            var (executor, wasActive, moved) = Apply(departmentId, item, existing.GetValueOrDefault(item.Id),
                withHistory.Contains(item.Id));
            saved.Add(executor);
            if ((wasActive && !executor.IsActive) || moved)
            {
                released.Add(executor.Id);
            }
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            db.AuditEntries.Add(audit);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var executor in saved)
        {
            await loadStore.SetActiveAsync(executor.Id, executor.IsActive, cancellationToken);
        }

        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();
        foreach (var id in released)
        {
            await ReassignOpenOrdersAsync(id, cancellationToken);
        }

        logger.LogInformation("Загружено сотрудников в отдел {DepartmentId}: {Count}", departmentId, incoming.Count);
    }

    /// <summary>Переносит данные из АИС или файла в сущность; новая — добавляется в контекст.</summary>
    /// <param name="hasHistory">У сотрудника уже есть история квалификации (иначе прежнее значение — «с начала»).</param>
    private (Executor Executor, bool WasActive, bool Moved) Apply(int departmentId, IncomingExecutor incoming,
        Executor? existing, bool hasHistory)
    {
        var now = clock.GetUtcNow();
        var executor = existing;
        if (executor is null)
        {
            executor = new Executor { Id = incoming.Id, DepartmentId = departmentId };
            db.Executors.Add(executor);
        }

        // история квалификации: справедливая доля за прошлые часы считается по тогдашнему значению
        var newQualification = incoming.QualificationWeight ?? executor.QualificationWeight;
        if (existing is not null && !hasHistory)
        {
            AddQualification(executor.Id, existing.QualificationWeight, DateTimeOffset.UnixEpoch); // прежнее — «с начала»
        }

        if (existing is null || newQualification != existing.QualificationWeight)
        {
            AddQualification(executor.Id, newQualification, now);
        }

        var wasActive = executor.IsActive;
        var moved = executor.DepartmentId != departmentId;
        executor.DepartmentId = departmentId;
        executor.FullName = incoming.FullName;
        executor.IsActive = incoming.IsActive;
        executor.DailyLimit = incoming.DailyLimit;
        if (incoming.QualificationWeight is { } qualification)
        {
            executor.QualificationWeight = qualification;
        }

        if (incoming.ExtraPercent is { } extra)
        {
            // сотрудник сам отметил в АИС, что готов взять больше нормы; не передано — прежняя отметка остаётся
            executor.ExtraPercent = extra;
        }

        executor.AttributesJson = JsonSerializer.Serialize(incoming.Attributes);
        executor.UpdatedAt = now;
        return (executor, wasActive, moved);
    }

    private void AddQualification(long executorId, decimal qualification, DateTimeOffset validFrom) =>
        db.ExecutorQualifications.Add(new ExecutorQualification
        {
            ExecutorId = executorId, Qualification = qualification, ValidFrom = validFrom,
        });

    /// <summary>Открытые заявки деактивированного исполнителя уходят другим.</summary>
    private async Task ReassignOpenOrdersAsync(long executorId, CancellationToken cancellationToken)
    {
        var orders = await db.Orders
            .Where(o => o.ExecutorId == executorId && o.Status == OrderStatus.Processed)
            .ToListAsync(cancellationToken);
        if (orders.Count == 0)
        {
            return;
        }

        foreach (var order in orders)
        {
            var snapshot = await directory.GetAsync(order.DepartmentId, cancellationToken);
            await loadStore.ReleaseAsync(order.Id, LoadRelease.Await, cancellationToken);
            var values = snapshot.Catalog.ParseStored(FieldOwner.Order, order.AttributesJson);
            await AssignAsync(order, values, snapshot, executorId, reopen: true, cancellationToken);
        }

        logger.LogInformation("Исполнитель {ExecutorId} ушёл или переведён, перераспределено заявок: {Count}",
            executorId, orders.Count);
    }

    /// <summary>Номер текущего часа: за него считается полученный вес — главный критерий выбора.</summary>
    public long CurrentHour() => ExecutorStats.HourOf(clock.GetUtcNow());

    public DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _timeZone).DateTime);

    /// <summary>
    /// Пробная проверка из конструктора: кому ушла бы заявка с такими параметрами прямо сейчас и почему.
    /// Ничего не назначает и не меняет счётчики.
    /// </summary>
    public async Task<AssignmentExplanation> PreviewAsync(int departmentId, long? parentId,
        IReadOnlyDictionary<string, JsonElement> attributes, CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var values = snapshot.Catalog.Parse(FieldOwner.Order, attributes, errors);
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }

        var explanation = new AssignmentExplanation { OrderWeight = snapshot.OrderWeight(values) };
        var matching = MatchCandidates(snapshot, values, explanation);
        var loads = await loadStore.GetLoadsAsync(Today(), CurrentHour(), cancellationToken);
        var weight = LoadMath.ToMilli(explanation.OrderWeight);

        if (parentId is { } id)
        {
            var parentExecutorId = await db.Orders.AsNoTracking()
                .Where(o => o.Id == id)
                .Select(o => o.ExecutorId)
                .FirstOrDefaultAsync(cancellationToken);
            var parentExecutor = matching.FirstOrDefault(e => e.Id == parentExecutorId);
            if (parentExecutor is not null)
            {
                explanation.Kind = AssignmentKind.Parent.ToString();
                explanation.ChosenExecutorId = parentExecutor.Id;
                explanation.Decision = $"{parentExecutor.FullName} ведёт родительскую заявку #{id} — суточный лимит не учитывается";
            }
            else
            {
                explanation.Notes.Add(parentExecutorId is null
                    ? $"у родительской заявки #{id} нет исполнителя — обычный выбор"
                    : $"исполнитель родительской заявки #{id} неактивен или не подходит — обычный выбор");
            }
        }

        // тот же порядок, что в скрипте выбора: сначала те, кто не набрал норму, затем излишки — добровольцам
        var extras = await ExtraDecisionsAsync(snapshot, matching, explanation.OrderWeight, cancellationToken);
        var best = new (ExecutorProfile Executor, ExecutorLoad Load)?[2];
        foreach (var executor in matching)
        {
            var load = loads.GetValueOrDefault(executor.Id) ?? new ExecutorLoad(0, 0, 0);
            var extra = extras.GetValueOrDefault(executor.Id);
            var slot = new CandidateSlot(executor.Id, LoadMath.ToMilli(executor.QualificationWeight), executor.DailyLimit,
                extra?.ExtraLimit);
            if (executor.DailyLimit is not null && load.AssignedToday >= slot.Cap)
            {
                explanation.Candidates.Add(new(executor.Id, executor.FullName, "daily_limit_exceeded",
                    LimitReason(load.AssignedToday, executor.DailyLimit, extra), null, load.AssignedToday));
                continue;
            }

            var tier = executor.DailyLimit is { } limit && load.AssignedToday >= limit ? 1 : 0;
            explanation.Candidates.Add(new(executor.Id, executor.FullName, tier == 0 ? "eligible" : "over_norm",
                tier == 0 ? null : $"норма набрана ({load.AssignedToday} из {executor.DailyLimit}), в режиме «больше нормы» " +
                                   $"до {extra?.ExtraLimit} — берёт только излишки",
                LoadMath.Score(load.HourWeightMilli, weight, slot.QualificationMilli), load.AssignedToday));
            if (best[tier] is not { } current || LoadMath.IsBetter(weight,
                    load.HourWeightMilli, load.OpenWeightMilli, slot.QualificationMilli, load.AssignedToday, executor.Id,
                    current.Load.HourWeightMilli, current.Load.OpenWeightMilli,
                    LoadMath.ToMilli(current.Executor.QualificationWeight), current.Load.AssignedToday, current.Executor.Id))
            {
                best[tier] = (executor, load);
            }
        }

        var overNorm = best[0] is null && best[1] is not null;
        var winner = (best[0] ?? best[1])?.Executor;
        if (explanation.ChosenExecutorId is null && winner is not null)
        {
            explanation.Kind = (overNorm ? AssignmentKind.Extra : AssignmentKind.Primary).ToString();
            explanation.ChosenExecutorId = winner.Id;
            explanation.ChosenScore = explanation.Candidates.First(c => c.ExecutorId == winner.Id).Score;
            explanation.Decision = overNorm
                ? $"{winner.FullName}: у всех подходящих норма на сегодня набрана, заявку берёт сверх нормы (режим «больше нормы»)"
                : $"{winner.FullName}: меньше всех получил за этот час среди подходящих (оценка {explanation.ChosenScore})";
        }
        else if (explanation.ChosenExecutorId is null)
        {
            explanation.Decision = matching.Count == 0
                ? "нет активного исполнителя, подходящего по параметрам — заявка будет ждать"
                : "у подходящих исполнителей исчерпан суточный лимит — заявка будет ждать";
        }

        if (explanation.ChosenExecutorId is { } chosen)
        {
            MarkChosen(explanation, chosen);
        }

        return explanation;
    }

    /// <summary>
    /// Почему конкретному сотруднику подходит (или нет) заявка с такими параметрами: каждое правило по отдельности,
    /// активность, суточная норма и нагрузка. Ничего не назначает. null — сотрудника в отделе нет.
    /// </summary>
    public async Task<ExecutorCheck?> CheckExecutorAsync(int departmentId, long executorId,
        IReadOnlyDictionary<string, JsonElement> attributes, CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(departmentId, cancellationToken);
        if (!snapshot.Executors.TryGetValue(executorId, out var executor))
        {
            return null;
        }

        var errors = new Dictionary<string, string[]>();
        var values = snapshot.Catalog.Parse(FieldOwner.Order, attributes, errors);
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }

        var weight = snapshot.OrderWeight(values);
        var rules = snapshot.Rules.Select(rule => rule.Explain(values, executor.Values)).ToList();
        var load = (await loadStore.GetLoadsAsync(Today(), CurrentHour(), cancellationToken)).GetValueOrDefault(executor.Id)
                   ?? new ExecutorLoad(0, 0, 0);
        var extra = (await ExtraDecisionsAsync(snapshot, [executor], weight, cancellationToken)).GetValueOrDefault(executor.Id);
        var matches = rules.All(r => r.Passed);

        string limit;
        var withinLimit = true;
        if (executor.DailyLimit is not { } norm)
        {
            limit = $"суточного лимита нет — сегодня получил {load.AssignedToday}";
        }
        else if (load.AssignedToday < norm)
        {
            limit = $"норма не набрана: {load.AssignedToday} из {norm}";
        }
        else if (extra?.ExtraLimit is { } cap && load.AssignedToday < cap)
        {
            limit = $"норма набрана ({load.AssignedToday} из {norm}), в режиме «больше нормы» — ещё до {cap}, только излишки";
        }
        else
        {
            withinLimit = false;
            limit = $"суточный лимит исчерпан: {load.AssignedToday} из {extra?.ExtraLimit ?? norm}"
                    + (extra?.Note is { } note ? $"; {note}" : "");
        }

        var qualification = LoadMath.ToMilli(executor.QualificationWeight);
        var summary = !executor.IsActive ? "сейчас не на работе — заявки не получает"
            : !matches ? "не подходит: не выполнено правило — см. отмеченное красным"
            : !withinLimit ? "подходит по правилам, но суточный лимит исчерпан"
            : "подходит: все правила выполнены, может получить заявку";
        return new ExecutorCheck(executor.Id, executor.FullName, executor.IsActive, matches && executor.IsActive && withinLimit,
            summary, rules, limit, weight, executor.QualificationWeight, load.OpenWeightMilli / 1000m,
            LoadMath.Score(load.HourWeightMilli, LoadMath.ToMilli(weight), qualification), load.HourWeightMilli / 1000m);
    }

    /// <summary>Отбор по активности и правилам конструктора; отсеянные сразу попадают в объяснение.</summary>
    private static List<ExecutorProfile> MatchCandidates(BalancerSnapshot snapshot,
        IReadOnlyDictionary<string, FieldValue> values, AssignmentExplanation explanation)
    {
        var matching = new List<ExecutorProfile>();
        foreach (var executor in snapshot.Executors.Values.OrderBy(e => e.Id))
        {
            if (!executor.IsActive)
            {
                explanation.Candidates.Add(new(executor.Id, executor.FullName, "inactive", "исполнитель неактивен", null, null));
                continue;
            }

            var failure = snapshot.FirstFailure(values, executor);
            if (failure is not null)
            {
                explanation.Candidates.Add(new(executor.Id, executor.FullName, "rule_failed", failure, null, null));
                continue;
            }

            matching.Add(executor);
        }

        return matching;
    }

    private async Task<BalanceResult> AssignAsync(Order order, IReadOnlyDictionary<string, FieldValue> values,
        BalancerSnapshot snapshot, long? previousExecutorId, bool reopen, CancellationToken cancellationToken,
        bool orphanReleased = false)
    {
        var explanation = new AssignmentExplanation { OrderWeight = order.Weight };
        var matching = MatchCandidates(snapshot, values, explanation);

        var weight = LoadMath.ToMilli(order.Weight);
        var day = Today();
        var kind = previousExecutorId is null ? AssignmentKind.Primary : AssignmentKind.Reassign;
        PickResult? pick = null;

        var preferred = await PreferredExecutorAsync(order, previousExecutorId, matching, explanation, cancellationToken);
        if (preferred is { } forced)
        {
            // исполнитель родительской или прежний исполнитель: суточный лимит не применяется
            var slot = new CandidateSlot(forced.Executor.Id, LoadMath.ToMilli(forced.Executor.QualificationWeight), null);
            // вернувшаяся с доработки заявка у того же исполнителя — не новая заявка за день
            var counts = forced.Kind != AssignmentKind.Secondary;
            var forcedPick = await loadStore.PickAsync(new PickRequest(order.Id, weight, day, reopen, [slot], counts,
                CurrentHour()), cancellationToken);
            if (forcedPick.Status == PickStatus.NoCandidate)
            {
                explanation.Notes.Add($"{forced.Executor.FullName} стал неактивен в момент назначения, выбираем из остальных");
            }
            else
            {
                pick = forcedPick;
                kind = forced.Kind;
            }
        }

        var extras = await ExtraDecisionsAsync(snapshot, matching, order.Weight, cancellationToken);
        pick ??= await loadStore.PickAsync(new PickRequest(order.Id, weight, day, reopen,
            matching.Select(e => new CandidateSlot(e.Id, LoadMath.ToMilli(e.QualificationWeight), e.DailyLimit,
                extras.GetValueOrDefault(e.Id)?.ExtraLimit)).ToList(), Hour: CurrentHour()),
            cancellationToken);

        AddLoadReport(explanation, pick, matching, weight, extras);

        if (pick.Status == PickStatus.AlreadyAssigned)
        {
            if (order.ExecutorId is null && !orphanReleased && IsOrphaned(pick))
            {
                // Redis держит заявку, а решения в базе нет: без отката заявка ждала бы вечно,
                // а исполнитель числился бы загруженным
                logger.LogWarning(
                    "Заявка {OrderId} с {HeldSince} числится за исполнителем {ExecutorId} только в Redis — откатываем и распределяем заново",
                    order.Id, pick.HeldSince, pick.ExecutorId);
                await loadStore.ReleaseAsync(order.Id, LoadRelease.Rollback, cancellationToken);
                return await AssignAsync(order, values, snapshot, previousExecutorId, reopen, cancellationToken,
                    orphanReleased: true);
            }

            return new BalanceResult(order.Id, BalanceOutcome.Assigned, pick.ExecutorId, null, null, Duplicate: true);
        }

        if (pick.Status == PickStatus.NoCandidate)
        {
            order.ExecutorId = null;
            order.PendingReason = matching.Count == 0
                ? "нет активного исполнителя, подходящего по параметрам"
                : "у подходящих исполнителей исчерпан суточный лимит";
            await db.SaveChangesAsync(cancellationToken);
            return BalanceResult.FromOrder(order);
        }

        var executorId = pick.ExecutorId!.Value;
        var chosen = snapshot.Executors[executorId];
        if (pick.Report.Any(r => r.ExecutorId == executorId && r.Verdict == "over_norm"))
        {
            kind = AssignmentKind.Extra;
        }

        explanation.Kind = kind.ToString();
        explanation.ChosenExecutorId = executorId;
        explanation.ChosenScore = explanation.Candidates.FirstOrDefault(c => c.ExecutorId == executorId)?.Score;
        explanation.Decision = kind switch
        {
            AssignmentKind.Parent => $"{chosen.FullName} ведёт родительскую заявку #{order.ParentId}",
            AssignmentKind.Secondary => $"{chosen.FullName} уже работал с этой заявкой",
            AssignmentKind.Extra =>
                $"{chosen.FullName}: у всех подходящих норма на сегодня набрана, заявку берёт сверх нормы (режим «больше нормы»)",
            _ => $"{chosen.FullName}: меньше всех получил за этот час среди подходящих (оценка {explanation.ChosenScore})",
        };
        MarkChosen(explanation, executorId);

        var now = clock.GetUtcNow();
        try
        {
            // снятие признака «текущее» и новое решение — в одной транзакции,
            // двумя шагами, чтобы не упереться в уникальный индекс по текущему назначению
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await db.Assignments.Where(a => a.OrderId == order.Id && a.IsCurrent)
                .ToListAsync(cancellationToken);
            if (current.Count > 0)
            {
                current.ForEach(a => a.IsCurrent = false);
                await db.SaveChangesAsync(cancellationToken);
            }

            db.Assignments.Add(new Assignment
            {
                DepartmentId = order.DepartmentId,
                OrderId = order.Id,
                ExecutorId = executorId,
                Kind = kind,
                OrderWeight = order.Weight,
                Score = explanation.ChosenScore ?? 0,
                IsCurrent = true,
                ExplanationJson = explanation.ToJson(),
                CreatedAt = now,
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                OrderId = order.Id,
                ExecutorId = executorId,
                CreatedAt = now,
                NextAttemptAt = now,
            });
            if (order.ExecutorId != executorId)
            {
                order.ReworkCount = 0; // доработки прежнего исполнителя новому в балл не засчитываются
            }

            order.ExecutorId = executorId;
            order.AssignedAt = now;
            order.PendingReason = null;
            await db.SaveChangesAsync(cancellationToken);
            await ExecutorStats.RecordAsync(db, now, order.DepartmentId, StatDeltas(kind, executorId, order.Weight),
                cancellationToken);
            if (kind is AssignmentKind.Primary or AssignmentKind.Reassign)
            {
                // кто мог получить эту заявку (излишки сверх нормы в эталон не входят — они никому не причитались): по этим группам считается эталон справедливого распределения
                await ExecutorStats.RecordEligibilityAsync(db, now, order.DepartmentId,
                    pick.Report.Where(r => r.Verdict == "eligible").Select(r => r.ExecutorId), order.Weight,
                    cancellationToken);
            }
            else
            {
                // назначение без выбора: эталону важно, в какую пятиминутку оно пришло
                await ExecutorStats.RecordPinnedAsync(db, now, order.DepartmentId, executorId, order.Weight,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось сохранить назначение заявки {OrderId}, откатываем счётчики", order.Id);
            await loadStore.ReleaseAsync(order.Id, LoadRelease.Rollback, CancellationToken.None);
            throw;
        }

        return new BalanceResult(order.Id, BalanceOutcome.Assigned, executorId, kind, null, Duplicate: false);
    }

    private async Task<(ExecutorProfile Executor, AssignmentKind Kind)?> PreferredExecutorAsync(Order order,
        long? previousExecutorId, List<ExecutorProfile> matching, AssignmentExplanation explanation,
        CancellationToken cancellationToken)
    {
        if (order.ParentId is { } parentId)
        {
            var parentExecutorId = await db.Orders.AsNoTracking()
                .Where(o => o.Id == parentId)
                .Select(o => o.ExecutorId)
                .FirstOrDefaultAsync(cancellationToken);
            var parentExecutor = matching.FirstOrDefault(e => e.Id == parentExecutorId);
            if (parentExecutor is not null)
            {
                return (parentExecutor, AssignmentKind.Parent);
            }

            explanation.Notes.Add(parentExecutorId is null
                ? $"у родительской заявки #{parentId} нет исполнителя"
                : $"исполнитель родительской заявки #{parentId} неактивен или не подходит по параметрам");
        }

        if (previousExecutorId is { } previousId)
        {
            var previous = matching.FirstOrDefault(e => e.Id == previousId);
            if (previous is not null)
            {
                return (previous, AssignmentKind.Secondary);
            }

            explanation.Notes.Add("прежний исполнитель неактивен или больше не подходит — перераспределение");
        }

        return null;
    }

    private bool IsOrphaned(PickResult pick) =>
        pick.HeldSince is { } since && clock.GetUtcNow() - since > OrphanedHoldAge;

    /// <summary>
    /// Режим «больше нормы» для подходящих сотрудников: потолок и причина, если режим сейчас не действует.
    /// Качество читается, только если в отделе кто-то в этом режиме.
    /// </summary>
    private async Task<Dictionary<long, ExtraDecision>> ExtraDecisionsAsync(BalancerSnapshot snapshot,
        List<ExecutorProfile> matching, decimal orderWeight, CancellationToken cancellationToken)
    {
        var volunteers = matching.Where(e => e.ExtraPercent > 0 && e.DailyLimit is not null).ToList();
        if (volunteers.Count == 0)
        {
            return [];
        }

        var scores = await quality.GetAsync(snapshot.DepartmentId, cancellationToken);
        return volunteers.ToDictionary(e => e.Id,
            e => snapshot.Motivation.Extra(e, scores.GetValueOrDefault(e.Id), orderWeight));
    }

    /// <summary>Показатели решения; заявки от родителя, вторичные и сверх нормы — не свободный выбор.</summary>
    private static StatDelta[] StatDeltas(AssignmentKind kind, long executorId, decimal orderWeight)
    {
        var free = kind is AssignmentKind.Primary or AssignmentKind.Reassign;
        return
        [
            new(executorId, Assigned: 1, AssignedWeight: orderWeight,
                Primary: kind == AssignmentKind.Primary ? 1 : 0,
                Reassign: kind == AssignmentKind.Reassign ? 1 : 0,
                Parent: kind == AssignmentKind.Parent ? 1 : 0,
                Secondary: kind == AssignmentKind.Secondary ? 1 : 0,
                FreeWeight: free ? orderWeight : 0,
                Extra: kind == AssignmentKind.Extra ? 1 : 0),
        ];
    }

    private static void AddLoadReport(AssignmentExplanation explanation, PickResult pick,
        List<ExecutorProfile> matching, long orderWeight, Dictionary<long, ExtraDecision> extras)
    {
        var reports = pick.Report.ToDictionary(r => r.ExecutorId);
        foreach (var executor in matching)
        {
            if (!reports.TryGetValue(executor.Id, out var report))
            {
                explanation.Candidates.Add(new(executor.Id, executor.FullName, "matched", null, null, null));
                continue;
            }

            var verdict = report.Verdict;
            var extra = extras.GetValueOrDefault(executor.Id);
            var reason = verdict switch
            {
                "inactive" => "исполнитель неактивен",
                "daily_limit_exceeded" => LimitReason(report.AssignedToday, executor.DailyLimit, extra),
                "over_norm" => $"норма набрана ({report.AssignedToday} из {executor.DailyLimit}), в режиме «больше нормы» " +
                               $"до {extra?.ExtraLimit} — берёт только излишки",
                _ => null,
            };
            decimal? score = verdict is "eligible" or "over_norm"
                ? LoadMath.Score(report.HourWeightMilli, orderWeight, LoadMath.ToMilli(executor.QualificationWeight))
                : null;
            explanation.Candidates.Add(new(executor.Id, executor.FullName, verdict, reason, score, report.AssignedToday));
        }
    }

    private static string LimitReason(int assignedToday, int? limit, ExtraDecision? extra) =>
        extra?.ExtraLimit is { } cap
            ? $"набрана норма и запас режима «больше нормы» ({assignedToday} из {cap})"
            : $"исчерпан суточный лимит ({assignedToday} из {limit})" + (extra?.Note is { } note ? $"; {note}" : "");

    private static void MarkChosen(AssignmentExplanation explanation, long executorId)
    {
        var index = explanation.Candidates.FindIndex(c => c.ExecutorId == executorId);
        if (index >= 0)
        {
            explanation.Candidates[index] = explanation.Candidates[index] with { Verdict = "chosen" };
        }
    }
}
