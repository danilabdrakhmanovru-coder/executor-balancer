namespace ExecutorBalancer.Domain;

/// <summary>Решение балансировщика. Для каждой заявки текущим может быть только одно.</summary>
public class Assignment
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long ExecutorId { get; set; }
    public AssignmentKind Kind { get; set; }
    public decimal OrderWeight { get; set; }
    public decimal Score { get; set; }
    public bool IsCurrent { get; set; }
    public string ExplanationJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
