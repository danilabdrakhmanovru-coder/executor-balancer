namespace ExecutorBalancer.Domain;

/// <summary>Назначение, которое нужно доставить в АИС. Пишется в той же транзакции, что и решение.</summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public long ExecutorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
