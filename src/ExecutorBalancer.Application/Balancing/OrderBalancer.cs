using System.Text.Json;
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
    IOptions<BalancerOptions> options,
    TimeProvider clock,
    ILogger<OrderBalancer> logger)
{
    private const string WaitingReason = "ожидает распределения";

    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public async Task<BalanceResult> ReceiveAsync(IncomingOrder incoming, CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(cancellationToken);
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
        order.Status = status;
        order.ClosedAt = status is OrderStatus.Accept or OrderStatus.Reject ? clock.GetUtcNow() : null;
        order.PendingReason = status == OrderStatus.Processed ? WaitingReason : null;
        await db.SaveChangesAsync(cancellationToken);

        if (status != OrderStatus.Processed)
        {
            return BalanceResult.FromOrder(order);
        }

        logger.LogDebug("Заявка {OrderId} вернулась в рассмотрение из статуса {Status}", order.Id, previousStatus);
        var snapshot = await directory.GetAsync(cancellationToken);
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

        var snapshot = await directory.GetAsync(cancellationToken);
        var values = snapshot.Catalog.ParseStored(FieldOwner.Order, order.AttributesJson);
        // reopen: заявка могла раньше принадлежать исполнителю и вернуться в очередь
        return await AssignAsync(order, values, snapshot, previousExecutorId: null, reopen: true, cancellationToken);
    }

    public async Task UpsertExecutorAsync(IncomingExecutor incoming, CancellationToken cancellationToken)
    {
        var snapshot = await directory.GetAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        snapshot.Catalog.Parse(FieldOwner.Executor, incoming.Attributes, errors);
        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }

        var executor = await db.Executors.FirstOrDefaultAsync(e => e.Id == incoming.Id, cancellationToken);
        if (executor is null)
        {
            executor = new Executor { Id = incoming.Id };
            db.Executors.Add(executor);
        }

        var wasActive = executor.IsActive;
        executor.FullName = incoming.FullName;
        executor.IsActive = incoming.IsActive;
        executor.DailyLimit = incoming.DailyLimit;
        if (incoming.QualificationWeight is { } qualification)
        {
            executor.QualificationWeight = qualification;
        }

        executor.AttributesJson = JsonSerializer.Serialize(incoming.Attributes);
        executor.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        await loadStore.SetActiveAsync(executor.Id, executor.IsActive, cancellationToken);
        await loadStore.BumpConfigVersionAsync(cancellationToken);
        directory.Invalidate();

        if (wasActive && !executor.IsActive)
        {
            await ReassignOpenOrdersAsync(executor.Id, cancellationToken);
        }
    }

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

        var snapshot = await directory.GetAsync(cancellationToken);
        foreach (var order in orders)
        {
            await loadStore.ReleaseAsync(order.Id, LoadRelease.Await, cancellationToken);
            var values = snapshot.Catalog.ParseStored(FieldOwner.Order, order.AttributesJson);
            await AssignAsync(order, values, snapshot, executorId, reopen: true, cancellationToken);
        }

        logger.LogInformation("Исполнитель {ExecutorId} деактивирован, перераспределено заявок: {Count}",
            executorId, orders.Count);
    }

    public DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), _timeZone).DateTime);

    private async Task<BalanceResult> AssignAsync(Order order, IReadOnlyDictionary<string, FieldValue> values,
        BalancerSnapshot snapshot, long? previousExecutorId, bool reopen, CancellationToken cancellationToken)
    {
        var explanation = new AssignmentExplanation { OrderWeight = order.Weight };
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

        var weight = LoadMath.ToMilli(order.Weight);
        var day = Today();
        var kind = previousExecutorId is null ? AssignmentKind.Primary : AssignmentKind.Reassign;
        PickResult? pick = null;

        var preferred = await PreferredExecutorAsync(order, previousExecutorId, matching, explanation, cancellationToken);
        if (preferred is { } forced)
        {
            // исполнитель родительской или прежний исполнитель: суточный лимит не применяется
            var slot = new CandidateSlot(forced.Executor.Id, LoadMath.ToMilli(forced.Executor.QualificationWeight), null);
            var forcedPick = await loadStore.PickAsync(new PickRequest(order.Id, weight, day, reopen, [slot]), cancellationToken);
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

        pick ??= await loadStore.PickAsync(new PickRequest(order.Id, weight, day, reopen,
            matching.Select(e => new CandidateSlot(e.Id, LoadMath.ToMilli(e.QualificationWeight), e.DailyLimit)).ToList()),
            cancellationToken);

        AddLoadReport(explanation, pick, matching, weight);

        if (pick.Status == PickStatus.AlreadyAssigned)
        {
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
        explanation.Kind = kind.ToString();
        explanation.ChosenExecutorId = executorId;
        explanation.ChosenScore = explanation.Candidates.FirstOrDefault(c => c.ExecutorId == executorId)?.Score;
        explanation.Decision = kind switch
        {
            AssignmentKind.Parent => $"{chosen.FullName} ведёт родительскую заявку #{order.ParentId}",
            AssignmentKind.Secondary => $"{chosen.FullName} уже работал с этой заявкой",
            _ => $"{chosen.FullName}: минимальная взвешенная нагрузка {explanation.ChosenScore} среди подходящих",
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
            order.ExecutorId = executorId;
            order.AssignedAt = now;
            order.PendingReason = null;
            await db.SaveChangesAsync(cancellationToken);
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

    private static void AddLoadReport(AssignmentExplanation explanation, PickResult pick,
        List<ExecutorProfile> matching, long orderWeight)
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
            var reason = verdict switch
            {
                "inactive" => "исполнитель неактивен",
                "daily_limit_exceeded" => $"исчерпан суточный лимит ({report.AssignedToday} из {executor.DailyLimit})",
                _ => null,
            };
            decimal? score = verdict == "eligible"
                ? LoadMath.Score(report.OpenWeightMilli, orderWeight, LoadMath.ToMilli(executor.QualificationWeight))
                : null;
            explanation.Candidates.Add(new(executor.Id, executor.FullName, verdict, reason, score, report.AssignedToday));
        }
    }

    private static void MarkChosen(AssignmentExplanation explanation, long executorId)
    {
        var index = explanation.Candidates.FindIndex(c => c.ExecutorId == executorId);
        if (index >= 0)
        {
            explanation.Candidates[index] = explanation.Candidates[index] with { Verdict = "chosen" };
        }
    }
}
