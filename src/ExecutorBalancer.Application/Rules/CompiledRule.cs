using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Rules;

public readonly record struct RuleOutcome(bool Passed, string? Reason)
{
    public static RuleOutcome Pass => new(true, null);

    public static RuleOutcome Fail(string reason) => new(false, reason);
}

/// <summary>Правило после проверки типов. Создаётся только через <see cref="RuleCompiler"/>.</summary>
public sealed class CompiledRule
{
    internal CompiledRule(int id, string name, FieldDefinition orderField, RuleOperator op, RuleTarget target,
        FieldDefinition? executorField, FieldDefinition? executorFieldUpper, FieldValue? constant,
        FieldValue? constantUpper, bool isStrict)
    {
        Id = id;
        Name = name;
        OrderField = orderField;
        Operator = op;
        Target = target;
        ExecutorField = executorField;
        ExecutorFieldUpper = executorFieldUpper;
        Constant = constant;
        ConstantUpper = constantUpper;
        IsStrict = isStrict;
    }

    public int Id { get; }
    public string Name { get; }
    public FieldDefinition OrderField { get; }
    public RuleOperator Operator { get; }
    public RuleTarget Target { get; }
    public FieldDefinition? ExecutorField { get; }
    public FieldDefinition? ExecutorFieldUpper { get; }
    public FieldValue? Constant { get; }
    public FieldValue? ConstantUpper { get; }
    public bool IsStrict { get; }

    /// <summary>
    /// Проверка исполнителя. Пустое значение с любой стороны — «ограничения нет»,
    /// если правило не строгое. У «между» пустая граница означает открытый интервал с этой стороны.
    /// </summary>
    public RuleOutcome Check(IReadOnlyDictionary<string, FieldValue> order,
        IReadOnlyDictionary<string, FieldValue> executor)
    {
        order.TryGetValue(OrderField.Key, out var left);
        FieldValue? right;
        FieldValue? upper = null;
        if (Target == RuleTarget.Constant)
        {
            right = Constant;
            upper = ConstantUpper;
        }
        else
        {
            executor.TryGetValue(ExecutorField!.Key, out right);
            if (ExecutorFieldUpper is not null)
            {
                executor.TryGetValue(ExecutorFieldUpper.Key, out upper);
            }
        }

        var missing = left is null || (Operator == RuleOperator.Between ? right is null && upper is null : right is null);
        if (missing)
        {
            return IsStrict ? RuleOutcome.Fail($"{Name}: значение не заполнено") : RuleOutcome.Pass;
        }

        return Evaluate(left!, right, upper) ? RuleOutcome.Pass : RuleOutcome.Fail(Describe(left!, right, upper));
    }

    /// <summary>Проверка условия только по заявке (для правил веса). Пустое значение — условие не выполнено.</summary>
    public bool Matches(IReadOnlyDictionary<string, FieldValue> order) =>
        order.TryGetValue(OrderField.Key, out var left) && Evaluate(left, Constant, ConstantUpper);

    private bool Evaluate(FieldValue left, FieldValue? right, FieldValue? upper) => Operator switch
    {
        RuleOperator.EqualTo => left.SameAs(right!),
        RuleOperator.NotEqualTo => !left.SameAs(right!),
        RuleOperator.GreaterThan => left.Number > right!.Number,
        RuleOperator.GreaterThanOrEqual => left.Number >= right!.Number,
        RuleOperator.LessThan => left.Number < right!.Number,
        RuleOperator.LessThanOrEqual => left.Number <= right!.Number,
        RuleOperator.In => right!.Items.Contains(left.Key),
        RuleOperator.NotIn => !right!.Items.Contains(left.Key),
        RuleOperator.Contains => left.IsArray
            ? left.Items.Contains(right!.Key)
            : left.Text.Contains(right!.Text, StringComparison.OrdinalIgnoreCase),
        RuleOperator.Between => (right is null || left.Number >= right.Number)
                                && (upper is null || left.Number <= upper.Number),
        _ => false,
    };

    private string Describe(FieldValue left, FieldValue? right, FieldValue? upper)
    {
        var expected = Operator == RuleOperator.Between
            ? $"[{right?.ToString() ?? "−∞"}; {upper?.ToString() ?? "+∞"}]"
            : right!.ToString();
        var source = Target == RuleTarget.Constant ? "" : $" ({ExecutorField!.Label} исполнителя)";
        return $"{Name}: у заявки {OrderField.Label} = {left}, требуется {OperatorText(Operator)} {expected}{source}";
    }

    public static string OperatorText(RuleOperator op) => op switch
    {
        RuleOperator.EqualTo => "равно",
        RuleOperator.NotEqualTo => "не равно",
        RuleOperator.GreaterThan => "больше",
        RuleOperator.GreaterThanOrEqual => "не меньше",
        RuleOperator.LessThan => "меньше",
        RuleOperator.LessThanOrEqual => "не больше",
        RuleOperator.In => "одно из",
        RuleOperator.NotIn => "не из",
        RuleOperator.Contains => "содержит",
        RuleOperator.Between => "в диапазоне",
        _ => op.ToString(),
    };
}

public sealed record CompiledWeightRule(CompiledRule Condition, decimal Weight);
