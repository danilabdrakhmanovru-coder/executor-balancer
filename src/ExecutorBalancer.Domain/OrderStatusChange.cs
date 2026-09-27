namespace ExecutorBalancer.Domain;

/// <summary>
/// Смена статуса заявки, пришедшая из АИС: на доработку, вернулась, решена, отклонена.
/// Пишется в той же транзакции, что и новый статус, — по ней строится путь заявки в карточке.
/// </summary>
public class OrderStatusChange
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public int DepartmentId { get; set; } = Department.DefaultId;
    public OrderStatus From { get; set; }
    public OrderStatus To { get; set; }

    /// <summary>У кого была заявка в момент смены статуса.</summary>
    public long? ExecutorId { get; set; }

    public DateTimeOffset At { get; set; }
}
