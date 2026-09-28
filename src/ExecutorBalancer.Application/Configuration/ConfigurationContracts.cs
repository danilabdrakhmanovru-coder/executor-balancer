using System.Text.Json;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Configuration;

/// <summary>Параметр заявки или исполнителя. Owner, Key и Type после создания не меняются.</summary>
/// <param name="OptionLabels">Подписи значений для экрана по порядку Options; null — оставить прежние.</param>
public sealed record FieldInput(FieldOwner? Owner, string? Key, string? Label, FieldType? Type, string[]? Options,
    string[]? OptionLabels = null);

/// <summary>
/// Правило подбора. Value — константа в JSON, если Target = Constant
/// (для «в диапазоне» — массив [от, до]).
/// </summary>
public sealed record RuleInput(
    string? Name,
    bool IsEnabled,
    int Priority,
    string? OrderField,
    RuleOperator? Operator,
    RuleTarget? Target,
    string? ExecutorField,
    string? ExecutorFieldUpper,
    JsonElement? Value,
    bool IsStrict);

/// <summary>Правило веса заявки: если условие выполнено — вес равен Weight. Срабатывает первое по приоритету.</summary>
public sealed record WeightRuleInput(
    bool IsEnabled,
    int Priority,
    string? OrderField,
    RuleOperator? Operator,
    JsonElement? Value,
    decimal Weight);

/// <param name="SampleMin">Правдоподобный диапазон числа для «Заполнить примером» (из шаблона сферы).</param>
public sealed record FieldView(int Id, FieldOwner Owner, string Key, string Label, FieldType Type, string[] Options,
    int UsedByRules, decimal? SampleMin = null, decimal? SampleMax = null, string[]? OptionLabels = null);

public sealed record RuleView(
    int Id,
    string Name,
    bool IsEnabled,
    int Priority,
    string OrderField,
    RuleOperator Operator,
    RuleTarget Target,
    string? ExecutorField,
    string? ExecutorFieldUpper,
    JsonElement? Value,
    bool IsStrict,
    string Text,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record WeightRuleView(
    int Id,
    bool IsEnabled,
    int Priority,
    string OrderField,
    RuleOperator Operator,
    JsonElement? Value,
    decimal Weight,
    string Text,
    string? Error);

public sealed record OperatorView(RuleOperator Value, string Label);

public sealed record ConfigurationView(
    IReadOnlyList<FieldView> Fields,
    IReadOnlyList<RuleView> Rules,
    IReadOnlyList<WeightRuleView> WeightRules,
    IReadOnlyList<OperatorView> Operators,
    decimal DefaultOrderWeight,
    ConfigurationLimits Limits);

public sealed record ConfigurationLimits(int FieldsPerOwner, int Rules, int WeightRules, int Options, int OptionLength);

/// <summary>Изменение нельзя применить из-за связанных данных — например, параметр используется в правиле.</summary>
public sealed class ConfigurationConflictException(string message) : Exception(message)
{
}

public sealed record AuditView(long Id, string Actor, string Action, string Entity, string EntityId, JsonElement? Data,
    DateTimeOffset CreatedAt);

public sealed record PresetView(string Id, string Title, string Description, string[] OrderFields, string[] ExecutorFields,
    bool IsCurrent);

/// <summary>
/// Настройки мотивации отдела: рейтинг, режим «больше нормы», защита от работы на количество, а также сколько
/// процентов сотрудников должно оставаться на работе (перерывы и увольнения из интерфейса).
/// </summary>
public sealed record MotivationInput(
    int FastCloseSeconds,
    decimal ReworkPenalty,
    decimal FastClosePenalty,
    int MaxExtraPercent,
    decimal QualityThreshold,
    decimal HeavyQualityThreshold,
    decimal HeavyWeight,
    int MinOnDutyPercent = 30);

public sealed record ExtraModeInput(int Percent);
