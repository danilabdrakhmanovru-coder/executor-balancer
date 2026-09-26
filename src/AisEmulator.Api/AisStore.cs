using System.Text.Json;

namespace AisEmulator.Api;

public sealed class AisExecutor
{
    public long Id { get; init; }
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; }
    public int? DailyLimit { get; set; }
    public decimal? QualificationWeight { get; set; }
    public Dictionary<string, JsonElement> Attributes { get; set; } = new();
}

public sealed class AisOrder
{
    public long Id { get; init; }
    public long? ParentId { get; init; }
    public string Status { get; set; } = "processed";
    public Dictionary<string, JsonElement> Attributes { get; init; } = new();
    public long? ExecutorId { get; set; }

    /// <summary>Номер последнего применённого назначения: более старое не перезапишет новое.</summary>
    public long AssignmentSequence { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
}

/// <summary>
/// Хранилище эмулятора в памяти: эмулятор изображает внешнюю систему и не является частью решения.
/// Объекты отдаются копиями, чтобы внешний код не менял их в обход блокировки.
/// </summary>
public sealed class AisStore
{
    public static readonly string[] Statuses = ["processed", "await", "accept", "reject"];

    private readonly object _gate = new();
    private readonly Dictionary<long, AisExecutor> _executors = new();
    private readonly Dictionary<long, AisOrder> _orders = new();
    private long _nextOrderId;
    private long _staleAssignments;

    public long StaleAssignments => Interlocked.Read(ref _staleAssignments);

    public AisExecutor UpsertExecutor(AisExecutor executor)
    {
        lock (_gate)
        {
            _executors[executor.Id] = executor;
            return Copy(executor);
        }
    }

    public AisExecutor? SetExecutorActive(long id, bool active)
    {
        lock (_gate)
        {
            if (!_executors.TryGetValue(id, out var executor))
            {
                return null;
            }

            executor.IsActive = active;
            return Copy(executor);
        }
    }

    public List<AisExecutor> Executors()
    {
        lock (_gate)
        {
            return _executors.Values.OrderBy(e => e.Id).Select(Copy).ToList();
        }
    }

    public AisOrder CreateOrder(long? parentId, Dictionary<string, JsonElement> attributes)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var order = new AisOrder
            {
                Id = ++_nextOrderId,
                ParentId = parentId,
                Attributes = attributes,
                CreatedAt = now,
                StatusChangedAt = now,
            };
            _orders[order.Id] = order;
            return Copy(order);
        }
    }

    public AisOrder? GetOrder(long id)
    {
        lock (_gate)
        {
            return _orders.TryGetValue(id, out var order) ? Copy(order) : null;
        }
    }

    public List<AisOrder> Orders(string? status, bool? assigned, int limit)
    {
        lock (_gate)
        {
            return _orders.Values
                .Where(o => status is null || o.Status == status)
                .Where(o => assigned is null || (o.ExecutorId is not null) == assigned)
                .OrderByDescending(o => o.Id)
                .Take(limit)
                .Select(Copy)
                .ToList();
        }
    }

    public AisOrder? SetStatus(long id, string status)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(id, out var order))
            {
                return null;
            }

            order.Status = status;
            order.StatusChangedAt = DateTimeOffset.UtcNow;
            return Copy(order);
        }
    }

    /// <summary>Записывает назначение, если оно новее уже записанного.</summary>
    public bool ApplyAssignment(long orderId, long executorId, long sequence)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return false;
            }

            if (sequence <= order.AssignmentSequence)
            {
                Interlocked.Increment(ref _staleAssignments);
                return false;
            }

            order.ExecutorId = executorId;
            order.AssignmentSequence = sequence;
            order.AssignedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public object Stats()
    {
        lock (_gate)
        {
            return new
            {
                Executors = _executors.Count,
                ActiveExecutors = _executors.Values.Count(e => e.IsActive),
                Orders = _orders.Count,
                ByStatus = Statuses.ToDictionary(s => s, s => _orders.Values.Count(o => o.Status == s)),
                ProcessedWithoutExecutor = _orders.Values.Count(o => o.Status == "processed" && o.ExecutorId is null),
                StaleAssignmentsIgnored = StaleAssignments,
            };
        }
    }

    private static AisExecutor Copy(AisExecutor e) => new()
    {
        Id = e.Id,
        FullName = e.FullName,
        IsActive = e.IsActive,
        DailyLimit = e.DailyLimit,
        QualificationWeight = e.QualificationWeight,
        Attributes = new Dictionary<string, JsonElement>(e.Attributes),
    };

    private static AisOrder Copy(AisOrder o) => new()
    {
        Id = o.Id,
        ParentId = o.ParentId,
        Status = o.Status,
        Attributes = o.Attributes,
        ExecutorId = o.ExecutorId,
        AssignmentSequence = o.AssignmentSequence,
        CreatedAt = o.CreatedAt,
        AssignedAt = o.AssignedAt,
        StatusChangedAt = o.StatusChangedAt,
    };
}
