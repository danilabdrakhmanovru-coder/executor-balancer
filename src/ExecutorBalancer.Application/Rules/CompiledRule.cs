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

    /// <summary>
    /// Подробный разбор для человека: что у заявки, что требуется от исполнителя, что у него есть и выполнено ли.
    /// Та же логика, что в <see cref="Check"/>, — результат всегда совпадает.
    /// </summary>
    public RuleExplanation Explain(IReadOnlyDictionary<string, FieldValue> order,
        IReadOnlyDictionary<string, FieldValue> executor)
    {
        order.TryGetValue(OrderField.Key, out var left);
        FieldValue? right;
        FieldValue? upper = null;
        string? source = null;
        if (Target == RuleTarget.Constant)
        {
            right = Constant;
            upper = ConstantUpper;
        }
        else
        {
            executor.TryGetValue(ExecutorField!.Key, out right);
            source = ExecutorField.Label;
            if (ExecutorFieldUpper is not null)
            {
                executor.TryGetValue(ExecutorFieldUpper.Key, out upper);
                source = $"{ExecutorField.Label} — {ExecutorFieldUpper.Label}";
            }
        }

        var expected = Operator == RuleOperator.Between
            ? $"от {Right(right) ?? "−∞"} до {Right(upper) ?? "+∞"}"
            : Right(right);
        var outcome = Check(order, executor);
        var missing = left is null || (Operator == RuleOperator.Between ? right is null && upper is null : right is null);
        var note = !missing ? null
            : IsStrict ? "значение не заполнено, а правило строгое"
            : left is null ? "у заявки значение не указано — правило не ограничивает"
            : "у исполнителя значение не указано — правило не ограничивает";
        return new RuleExplanation(Name, OrderField.Label, Left(left), OperatorText(Operator), source, expected,
            outcome.Passed, note, IsStrict);
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
            ? $"[{Right(right) ?? "−∞"}; {Right(upper) ?? "+∞"}]"
            : Right(right);
        var source = Target == RuleTarget.Constant ? "" : $" ({ExecutorField!.Label} исполнителя)";
        return $"{Name}: у заявки {OrderField.Label} = {Left(left)}, требуется {OperatorText(Operator)} {expected}{source}";
    }

    // значения для человека: коды справочника — подписями (ORDER_3 → «Претензия»); список — в квадратных скобках
    private string? Left(FieldValue? value) => value is null ? null : Text(OrderField, value);

    private string? Right(FieldValue? value) => value is null ? null
        : Text(Target == RuleTarget.Constant ? OrderField : ExecutorField, value);

    private static string Text(FieldDefinition? field, FieldValue value) => value.IsArray
        ? "[" + string.Join(", ", FieldText.Items(field, value)) + "]"
        : value.Type is FieldType.Enum or FieldType.String ? field?.Show(value.Text) ?? value.Text : value.ToString();

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

/// <param name="OrderValue">Значение у заявки; null — не указано.</param>
/// <param name="Source">Откуда берётся требование: параметр исполнителя или null для константы.</param>
/// <param name="Expected">Значение у исполнителя (или константа); null — не указано.</param>
public sealed record RuleExplanation(string Rule, string OrderField, string? OrderValue, string Operator, string? Source,
    string? Expected, bool Passed, string? Note, bool IsStrict);
