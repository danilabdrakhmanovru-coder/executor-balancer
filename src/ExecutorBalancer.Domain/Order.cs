namespace ExecutorBalancer.Domain;

public class Order
{
    /// <summary>Идентификатор заявки во внешней АИС.</summary>
    public long Id { get; set; }

    public int DepartmentId { get; set; } = Department.DefaultId;

    public long? ParentId { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Weight { get; set; }
    public string AttributesJson { get; set; } = "{}";
    public long? ExecutorId { get; set; }

    /// <summary>Почему заявка пока без исполнителя.</summary>
    public string? PendingReason { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
