using System.Text.Json;

namespace ExecutorBalancer.Application.Balancing;

/// <param name="Verdict">rule_failed, inactive, daily_limit_exceeded, eligible, chosen, matched.</param>
public sealed record CandidateVerdict(
    long ExecutorId,
    string Name,
    string Verdict,
    string? Reason,
    decimal? Score,
    int? AssignedToday);

/// <summary>Объяснение решения: кто рассматривался, почему отсеян, какой score, почему выбран.</summary>
public sealed class AssignmentExplanation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public decimal OrderWeight { get; init; }
    public string? Kind { get; set; }
    public long? ChosenExecutorId { get; set; }
    public decimal? ChosenScore { get; set; }
    public string? Decision { get; set; }
    public List<string> Notes { get; init; } = [];
    public List<CandidateVerdict> Candidates { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AssignmentExplanation? FromJson(string json) =>
        JsonSerializer.Deserialize<AssignmentExplanation>(json, Json);
}

/// <summary>Разбор «почему этому сотруднику подходит заявка»: правила по отдельности, норма, нагрузка.</summary>
/// <param name="Score">Оценка, если бы заявка ушла ему: (вес за этот час + вес заявки) / квалификация.</param>
public sealed record ExecutorCheck(
    long ExecutorId,
    string Name,
    bool IsActive,
    bool CanTake,
    string Summary,
    IReadOnlyList<Rules.RuleExplanation> Rules,
    string Limit,
    decimal OrderWeight,
    decimal Qualification,
    decimal OpenWeight,
    decimal Score,
    decimal HourWeight = 0);
