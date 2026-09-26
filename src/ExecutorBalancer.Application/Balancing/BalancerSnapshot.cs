using ExecutorBalancer.Application.Rules;

namespace ExecutorBalancer.Application.Balancing;

public sealed record ExecutorProfile(
    long Id,
    string FullName,
    bool IsActive,
    int? DailyLimit,
    decimal QualificationWeight,
    IReadOnlyDictionary<string, FieldValue> Values);

/// <summary>Неизменяемый срез конфигурации: справочник полей, правила и исполнители.</summary>
public sealed class BalancerSnapshot
{
    public required long Version { get; init; }
    public required FieldCatalog Catalog { get; init; }
    public required IReadOnlyList<CompiledRule> Rules { get; init; }
    public required IReadOnlyDictionary<int, string> RuleErrors { get; init; }
    public required IReadOnlyList<CompiledWeightRule> WeightRules { get; init; }
    public required IReadOnlyDictionary<long, ExecutorProfile> Executors { get; init; }
    public required decimal DefaultOrderWeight { get; init; }

    public decimal OrderWeight(IReadOnlyDictionary<string, FieldValue> order) =>
        WeightRules.FirstOrDefault(rule => rule.Condition.Matches(order))?.Weight ?? DefaultOrderWeight;

    /// <summary>Первое нарушенное правило или null, если исполнитель подходит.</summary>
    public string? FirstFailure(IReadOnlyDictionary<string, FieldValue> order, ExecutorProfile executor)
    {
        foreach (var rule in Rules)
        {
            var outcome = rule.Check(order, executor.Values);
            if (!outcome.Passed)
            {
                return outcome.Reason;
            }
        }

        return null;
    }
}
