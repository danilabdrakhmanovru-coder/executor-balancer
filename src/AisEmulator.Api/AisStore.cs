using System.Text.Json;

namespace AisEmulator.Api;

public sealed class AisExecutor
{
    public long Id { get; init; }

    /// <summary>Код отдела балансировщика; null — основной отдел (старые адреса API).</summary>
    public string? Department { get; set; }

    public string FullName { get; set; } = "";
    public bool IsActive { get; set; }
    public int? DailyLimit { get; set; }
    public decimal? QualificationWeight { get; set; }
    public Dictionary<string, JsonElement> Attributes { get; set; } = new();
}

public sealed class AisOrder
{
    public long Id { get; init; }
    public string? Department { get; init; }
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

    /// <summary>Фильтр «без отдела» (основной отдел по старым адресам); null в фильтре — все отделы.</summary>
    public const string NoDepartment = "";

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

    public List<AisExecutor> Executors(string? department = null)
    {
        lock (_gate)
        {
            return _executors.Values.Where(e => department is null || (e.Department ?? NoDepartment) == department)
                .OrderBy(e => e.Id).Select(Copy).ToList();
        }
    }

    /// <summary>
    /// Номера заявок не повторяются: эмулятор держит их в памяти и после перезапуска начал бы с 1,
    /// а балансировщик принял бы новые заявки за повторы старых. Пульт сообщает, с какого номера продолжать.
    /// </summary>
    public void ContinueOrderIdsFrom(long next)
    {
        lock (_gate)
        {
            _nextOrderId = Math.Max(_nextOrderId, next - 1);
        }
    }

    public AisOrder CreateOrder(string? department, long? parentId, Dictionary<string, JsonElement> attributes)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var order = new AisOrder
            {
                Id = ++_nextOrderId,
                Department = department,
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

    public List<AisOrder> Orders(string? status, bool? assigned, int limit, string? department = null)
    {
        lock (_gate)
        {
            return _orders.Values
                .Where(o => department is null || (o.Department ?? NoDepartment) == department)
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

    /// <summary>Сводка по отделу (null — по всей АИС).</summary>
    public object Stats(string? department = null)
    {
        lock (_gate)
        {
            var executors = _executors.Values.Where(e => department is null || (e.Department ?? NoDepartment) == department).ToList();
            var orders = _orders.Values.Where(o => department is null || (o.Department ?? NoDepartment) == department).ToList();
            return new
            {
                Executors = executors.Count,
                ActiveExecutors = executors.Count(e => e.IsActive),
                Orders = orders.Count,
                ByStatus = Statuses.ToDictionary(s => s, s => orders.Count(o => o.Status == s)),
                ProcessedWithoutExecutor = orders.Count(o => o.Status == "processed" && o.ExecutorId is null),
                StaleAssignmentsIgnored = StaleAssignments,
            };
        }
    }

    private static AisExecutor Copy(AisExecutor e) => new()
    {
        Id = e.Id,
        Department = e.Department,
        FullName = e.FullName,
        IsActive = e.IsActive,
        DailyLimit = e.DailyLimit,
        QualificationWeight = e.QualificationWeight,
        Attributes = new Dictionary<string, JsonElement>(e.Attributes),
    };

    private static AisOrder Copy(AisOrder o) => new()
    {
        Id = o.Id,
        Department = o.Department,
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
