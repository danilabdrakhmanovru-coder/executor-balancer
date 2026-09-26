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
