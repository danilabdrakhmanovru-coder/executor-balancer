using System.Globalization;
using System.Text;
using System.Text.Json;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Configuration;

/// <summary>Как сотрудник соответствует параметру заявки.</summary>
public enum SphereMatch
{
    /// <summary>Не влияет на выбор сотрудника (только вес и отчёты).</summary>
    None,

    /// <summary>Справочник: у сотрудника список значений, с которыми он работает; заявка — «одно из».</summary>
    Skills,

    /// <summary>Число: у сотрудника максимум («допуск»), значение заявки не больше.</summary>
    Max,

    /// <summary>Число: у сотрудника минимум, значение заявки не меньше.</summary>
    Min,
}

/// <param name="Label">Название параметра заявки, например «Язык клиента».</param>
/// <param name="Options">Значения справочника (для Enum).</param>
/// <param name="ExecutorLabel">Название парного параметра сотрудника, например «Языки»; пусто — подберётся.</param>
/// <param name="HeavyValue">Когда заявка сложнее: значение справочника, true или порог числа.</param>
/// <param name="HeavyWeight">Вес сложной заявки; null — вес не меняется.</param>
public sealed record SphereParamInput(
    string? Label,
    FieldType? Type,
    string[]? Options,
    SphereMatch Match,
    string? ExecutorLabel,
    JsonElement? HeavyValue,
    decimal? HeavyWeight);

/// <summary>Своя сфера: параметры заявки и то, как по ним подбирается сотрудник.</summary>
public sealed record SphereInput(string? Title, SphereParamInput[]? Params);

/// <summary>
/// Мастер новой сферы: из описания параметров заявки строит парные параметры сотрудника, правила подбора
/// и правила веса. Ключи — транслитом названия. Все правила проверяются тем же компилятором, что и в конструкторе,
/// поэтому отдел не может получить правило, которое «упадёт» на заявке.
/// </summary>
public static class SphereBuilder
{
    public const int MaxParams = 20;
    public const int MaxTitleLength = 120;

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
    };

    public sealed record Result(string Title, List<FieldDefinition> Fields, List<Rule> Rules, List<WeightRule> WeightRules);

    public static Result Build(SphereInput input, int departmentId, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        var title = input.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitleLength || title.Any(char.IsControl))
        {
            errors["sphere.title"] = [$"название сферы: от 1 до {MaxTitleLength} символов"];
        }

        var items = input.Params ?? [];
        if (items.Length is 0 or > MaxParams)
        {
            errors["sphere.params"] = [$"от 1 до {MaxParams} параметров заявки"];
        }

        var keys = new HashSet<string>();
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<FieldDefinition>();
        var rules = new List<Rule>();
        var weights = new List<WeightRule>();
        for (var i = 0; i < Math.Min(items.Length, MaxParams); i++)
        {
            var p = items[i];
            var at = $"sphere.params[{i}]";
            var label = Label(p.Label, $"{at}.label", errors);
            if (label.Length > 0 && !labels.Add(label))
            {
                errors[$"{at}.label"] = ["параметры с одинаковым названием"];
            }

            if (p.Type is not (FieldType.Enum or FieldType.Number or FieldType.Boolean or FieldType.String))
            {
                errors[$"{at}.type"] = ["тип: справочник, число, да/нет или строка"];
                continue;
            }

            var type = p.Type.Value;
            var options = type == FieldType.Enum ? Options(p.Options, $"{at}.options", errors) : [];
            var allowed = type switch
            {
                FieldType.Enum => p.Match is SphereMatch.None or SphereMatch.Skills,
                FieldType.Number => p.Match is SphereMatch.None or SphereMatch.Max or SphereMatch.Min,
                _ => p.Match == SphereMatch.None,
            };
            if (!allowed)
            {
                errors[$"{at}.match"] = ["такое сопоставление не подходит к типу параметра"];
                continue;
            }

            var key = Key(label, "p", keys);
            fields.Add(new FieldDefinition { DepartmentId = departmentId, Owner = FieldOwner.Order, Key = key, Label = label, Type = type, Options = options });

            if (p.Match != SphereMatch.None)
            {
                var executorLabel = string.IsNullOrWhiteSpace(p.ExecutorLabel)
                    ? p.Match switch
                    {
                        SphereMatch.Skills => $"{label}: с чем работает",
                        SphereMatch.Max => $"{label}: максимум",
                        _ => $"{label}: минимум",
                    }
                    : Label(p.ExecutorLabel, $"{at}.executorLabel", errors);
                var executorKey = Key(executorLabel, "s", keys);
                fields.Add(new FieldDefinition
                {
                    DepartmentId = departmentId, Owner = FieldOwner.Executor, Key = executorKey, Label = executorLabel,
                    Type = p.Match == SphereMatch.Skills ? FieldType.Array : FieldType.Number,
                    Options = p.Match == SphereMatch.Skills ? options : [],
                });
                rules.Add(new Rule
                {
                    DepartmentId = departmentId,
                    Name = label,
                    Priority = (rules.Count + 1) * 10,
                    OrderField = key,
                    Operator = p.Match switch
                    {
                        SphereMatch.Skills => RuleOperator.In,
                        SphereMatch.Max => RuleOperator.LessThanOrEqual,
                        _ => RuleOperator.GreaterThanOrEqual,
                    },
                    Target = RuleTarget.ExecutorField,
                    ExecutorField = executorKey,
                    UpdatedAt = now,
                });
            }

            if (p.HeavyWeight is { } weight)
            {
                if (weight is < ConfigurationService.MinWeight or > ConfigurationService.MaxWeight)
                {
                    errors[$"{at}.heavyWeight"] = [$"вес от {ConfigurationService.MinWeight} до {ConfigurationService.MaxWeight}"];
                    continue;
                }

                var value = HeavyValue(type, options, p.HeavyValue, $"{at}.heavyValue", errors);
                if (value is null)
                {
                    continue;
                }

                weights.Add(new WeightRule
                {
                    DepartmentId = departmentId,
                    Priority = (weights.Count + 1) * 10,
                    OrderField = key,
                    Operator = type == FieldType.Number ? RuleOperator.GreaterThanOrEqual : RuleOperator.EqualTo,
                    ValueJson = value,
                    Weight = weight,
                });
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidInputException(errors);
        }

        // та же проверка, что при сохранении правила в конструкторе
        var catalog = new FieldCatalog(fields);
        try
        {
            rules.ForEach(r => RuleCompiler.Compile(r, catalog));
            weights.ForEach(w => RuleCompiler.CompileWeight(w, catalog));
        }
        catch (RuleValidationException ex)
        {
            throw new InvalidInputException(new Dictionary<string, string[]> { ["sphere"] = [ex.Message] });
        }

        return new Result(title, fields, rules, weights);
    }

    /// <summary>Ключ для АИС из названия: «Язык клиента» → yazyk_klienta; занятый — с номером.</summary>
    public static string Key(string label, string fallback, HashSet<string> taken)
    {
        var text = new StringBuilder();
        foreach (var ch in label.ToLower(CultureInfo.GetCultureInfo("ru-RU")))
        {
            if (Translit.TryGetValue(ch, out var latin))
            {
                text.Append(latin);
            }
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                text.Append(ch);
            }
            else if (text.Length > 0 && text[^1] != '_')
            {
                text.Append('_');
            }
        }

        var key = text.ToString().Trim('_');
        if (key.Length > 40)
        {
            key = key[..40].TrimEnd('_');
        }

        if (key.Length == 0 || !char.IsAsciiLetterLower(key[0]))
        {
            key = $"{fallback}_{key}".TrimEnd('_');
        }

        var candidate = key;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{key}_{n}";
        }

        return candidate;
    }

    private static string Label(string? value, string name, Dictionary<string, string[]> errors)
    {
        var label = value?.Trim() ?? "";
        if (label.Length is 0 or > ConfigurationService.MaxLabelLength || label.Any(char.IsControl))
        {
            errors[name] = [$"название: от 1 до {ConfigurationService.MaxLabelLength} символов"];
        }

        return label;
    }

    private static string[] Options(string[]? values, string name, Dictionary<string, string[]> errors)
    {
        var list = (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).ToList();
        if (list.Count is < 2 or > ConfigurationService.MaxOptions)
        {
            errors[name] = [$"справочник: от 2 до {ConfigurationService.MaxOptions} значений"];
        }
        else if (list.Any(v => v.Length > ConfigurationService.MaxOptionLength || v.Any(char.IsControl)))
        {
            errors[name] = [$"значение справочника не длиннее {ConfigurationService.MaxOptionLength} символов"];
        }
        else if (list.Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            errors[name] = ["значения справочника повторяются"];
        }

        return list.ToArray();
    }

    private static string? HeavyValue(FieldType type, string[] options, JsonElement? value, string name,
        Dictionary<string, string[]> errors)
    {
        switch (type)
        {
            case FieldType.Enum when value is { ValueKind: JsonValueKind.String } s && options.Contains(s.GetString()):
                return JsonSerializer.Serialize(s.GetString());
            case FieldType.Number when value is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var number):
                return JsonSerializer.Serialize(number);
            case FieldType.Boolean:
                return "true";
            case FieldType.String when value is { ValueKind: JsonValueKind.String } t && t.GetString()!.Trim().Length is > 0 and <= 500:
                return JsonSerializer.Serialize(t.GetString()!.Trim());
            default:
                errors[name] = ["укажите, при каком значении заявка сложнее"];
                return null;
        }
    }
}
