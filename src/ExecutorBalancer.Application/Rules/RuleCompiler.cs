using System.Text.Json;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Rules;

public sealed class RuleValidationException(string message) : Exception(message)
{
}

/// <summary>Проверяет правило против справочника полей и превращает его в <see cref="CompiledRule"/>.</summary>
public static class RuleCompiler
{
    public static CompiledRule Compile(Rule rule, FieldCatalog catalog)
    {
        var left = catalog.Find(FieldOwner.Order, rule.OrderField)
                   ?? throw new RuleValidationException($"у заявки нет параметра «{rule.OrderField}»");
        var op = rule.Operator;

        if (rule.Target == RuleTarget.ExecutorField)
        {
            var right = catalog.Find(FieldOwner.Executor, rule.ExecutorField)
                        ?? throw new RuleValidationException($"у исполнителя нет параметра «{rule.ExecutorField}»");
            FieldDefinition? upper = null;
            if (op == RuleOperator.Between)
            {
                upper = catalog.Find(FieldOwner.Executor, rule.ExecutorFieldUpper)
                        ?? throw new RuleValidationException("для «в диапазоне» укажите поле верхней границы");
                Require(left.Type == FieldType.Number && right.Type == FieldType.Number && upper.Type == FieldType.Number,
                    "«в диапазоне» применим только к числам");
            }
            else
            {
                Require(Compatible(op, left.Type, right.Type),
                    $"оператор «{CompiledRule.OperatorText(op)}» нельзя применить к «{left.Label}» ({left.Type}) и «{right.Label}» ({right.Type})");
            }

            return new CompiledRule(rule.Id, rule.Name, left, op, rule.Target, right, upper, null, null, rule.IsStrict);
        }

        var (constant, constantUpper) = ParseConstant(op, left, rule.ValueJson);
        return new CompiledRule(rule.Id, rule.Name, left, op, rule.Target, null, null, constant, constantUpper,
            rule.IsStrict);
    }

    public static CompiledWeightRule CompileWeight(WeightRule rule, FieldCatalog catalog)
    {
        Require(rule.Weight > 0, "вес должен быть положительным");
        var condition = Compile(new Rule
        {
            Id = rule.Id,
            Name = $"вес {rule.Weight}",
            OrderField = rule.OrderField,
            Operator = rule.Operator,
            Target = RuleTarget.Constant,
            ValueJson = rule.ValueJson,
        }, catalog);
        return new CompiledWeightRule(condition, rule.Weight);
    }

    private static bool Compatible(RuleOperator op, FieldType left, FieldType right) => op switch
    {
        RuleOperator.EqualTo or RuleOperator.NotEqualTo =>
            (left == FieldType.Array && right == FieldType.Array) || (left != FieldType.Array && Kind(left) == Kind(right)),
        RuleOperator.GreaterThan or RuleOperator.GreaterThanOrEqual or RuleOperator.LessThan
            or RuleOperator.LessThanOrEqual => left == FieldType.Number && right == FieldType.Number,
        RuleOperator.In or RuleOperator.NotIn => left != FieldType.Array && right == FieldType.Array,
        RuleOperator.Contains => (left == FieldType.Array && right != FieldType.Array) || (IsText(left) && IsText(right)),
        _ => false,
    };

    private static char Kind(FieldType type) => type switch
    {
        FieldType.Number => 'n',
        FieldType.Boolean => 'b',
        FieldType.Array => 'a',
        _ => 't',
    };

    private static bool IsText(FieldType type) => type is FieldType.String or FieldType.Enum;

    private static (FieldValue Value, FieldValue? Upper) ParseConstant(RuleOperator op, FieldDefinition left, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RuleValidationException("не указано значение для сравнения");
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new RuleValidationException("значение должно быть корректным JSON");
        }

        if (op == RuleOperator.Between)
        {
            Require(left.Type == FieldType.Number, "«в диапазоне» применим только к числам");
            Require(root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 2,
                "для «в диапазоне» укажите массив из двух чисел: [от, до]");
            var bounds = root.EnumerateArray()
                .Select(item => Parse(item, FieldType.Number, []))
                .ToArray();
            Require(bounds[0].Number <= bounds[1].Number, "нижняя граница больше верхней");
            return (bounds[0], bounds[1]);
        }

        var value = op switch
        {
            RuleOperator.EqualTo or RuleOperator.NotEqualTo => Parse(root, left.Type, left.Options),
            RuleOperator.GreaterThan or RuleOperator.GreaterThanOrEqual or RuleOperator.LessThan
                or RuleOperator.LessThanOrEqual when left.Type == FieldType.Number => Parse(root, FieldType.Number, []),
            RuleOperator.In or RuleOperator.NotIn when left.Type != FieldType.Array =>
                Parse(root, FieldType.Array, left.Type == FieldType.Enum ? left.Options : []),
            RuleOperator.Contains when left.Type == FieldType.Array =>
                Parse(root, left.Options.Length > 0 ? FieldType.Enum : FieldType.String, left.Options),
            RuleOperator.Contains when IsText(left.Type) => Parse(root, FieldType.String, []),
            _ => throw new RuleValidationException(
                $"оператор «{CompiledRule.OperatorText(op)}» нельзя применить к «{left.Label}» ({left.Type})"),
        };
        return (value, null);
    }

    private static FieldValue Parse(JsonElement json, FieldType type, IReadOnlyCollection<string> options)
    {
        if (!FieldValue.TryParse(json, type, options, out var value, out var error))
        {
            throw new RuleValidationException($"значение: {error}");
        }

        return value ?? throw new RuleValidationException("не указано значение для сравнения");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new RuleValidationException(message);
        }
    }
}
