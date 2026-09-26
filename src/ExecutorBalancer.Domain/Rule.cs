namespace ExecutorBalancer.Domain;

/// <summary>
/// Условие подбора: «поле заявки — оператор — поле исполнителя или константа».
/// Для оператора Between с полями исполнителя используются два поля: нижняя и верхняя граница.
/// </summary>
public class Rule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
    public string OrderField { get; set; } = "";
    public RuleOperator Operator { get; set; }
    public RuleTarget Target { get; set; }
    public string? ExecutorField { get; set; }
    public string? ExecutorFieldUpper { get; set; }

    /// <summary>Константа в JSON, если Target = Constant.</summary>
    public string? ValueJson { get; set; }

    /// <summary>Строгое правило: пустое значение означает «не подходит».</summary>
    public bool IsStrict { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
