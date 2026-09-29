using System.Text.Json;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Executors;

/// <summary>
/// Навыки сотрудника, который подходит под любую заявку отдела, — для демонстрации «двух одинаковых сотрудников»:
/// тогда заявку решает только нагрузка. Собираются по справочнику и правилам отдела, поэтому работают в любой сфере:
/// список — все значения; число — по роли в правилах (нижняя граница диапазона и «заявка ≥ сотрудника» — 0,
/// верхняя граница и «заявка ≤ сотрудника» — очень большое); да/нет — да.
/// </summary>
public static class AllCapable
{
    public const decimal Unlimited = 1_000_000_000m;

    public static Dictionary<string, JsonElement> Skills(IEnumerable<FieldDefinition> executorFields, IEnumerable<Rule> rules)
    {
        var active = rules.Where(r => r.IsEnabled && r.Target == RuleTarget.ExecutorField).ToList();
        bool Lower(string key) => active.Any(r => (r.Operator == RuleOperator.Between && r.ExecutorField == key)
            || (r.Operator is RuleOperator.GreaterThan or RuleOperator.GreaterThanOrEqual && r.ExecutorField == key));

        var skills = new Dictionary<string, JsonElement>();
        foreach (var field in executorFields.Where(f => f.Owner == FieldOwner.Executor))
        {
            object? value = field.Type switch
            {
                FieldType.Array => field.Options,
                FieldType.Enum => field.Options.FirstOrDefault(),
                FieldType.Boolean => true,
                FieldType.Number => Lower(field.Key) ? 0m : Unlimited,
                _ => field.Options.FirstOrDefault() ?? field.Label,
            };
            if (value is not null)
            {
                skills[field.Key] = JsonSerializer.SerializeToElement(value);
            }
        }

        return skills;
    }
}
