using System.Text.Json;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Balancing;

public enum BalanceOutcome
{
    Assigned,
    Pending,
    Stored,
}

public sealed record BalanceResult(
    long OrderId,
    BalanceOutcome Outcome,
    long? ExecutorId,
    AssignmentKind? Kind,
    string? Reason,
    bool Duplicate)
{
    public static BalanceResult FromOrder(Order order, bool duplicate = false) => new(
        order.Id,
        order.ExecutorId is not null && order.Status is OrderStatus.Processed ? BalanceOutcome.Assigned
            : order.Status is OrderStatus.Processed ? BalanceOutcome.Pending : BalanceOutcome.Stored,
        order.ExecutorId,
        null,
        order.PendingReason,
        duplicate);
}

public sealed record IncomingOrder(
    long Id,
    long? ParentId,
    OrderStatus Status,
    IReadOnlyDictionary<string, JsonElement> Attributes);

public sealed record IncomingExecutor(
    long Id,
    string FullName,
    bool IsActive,
    int? DailyLimit,
    decimal? QualificationWeight,
    IReadOnlyDictionary<string, JsonElement> Attributes);
