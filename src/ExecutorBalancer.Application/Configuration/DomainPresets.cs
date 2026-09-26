using System.Text.Json;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Application.Configuration;

/// <summary>
/// Подсказка генератору демо-данных: какие значения параметра правдоподобны.
/// На распределение не влияет — только на то, какие заявки и исполнителей создаёт эмулятор АИС.
/// </summary>
public sealed record GeneratorHint(
    double[]? Weights = null,
    decimal? Min = null,
    decimal? Max = null,
    decimal[]? Choices = null,
    bool LogScale = false,
    double? Probability = null,
    int? MinItems = null,
    int? MaxItems = null,
    string[]? Required = null);

public sealed record PresetField(FieldOwner Owner, string Key, string Label, FieldType Type, string[] Options,
    GeneratorHint? Hint = null);

public sealed record PresetRule(string Name, string OrderField, RuleOperator Operator, string? ExecutorField,
    string? ExecutorFieldUpper = null);

public sealed record PresetWeight(string OrderField, RuleOperator Operator, object Value, decimal Weight);

/// <summary>Шаблон сферы: готовый набор параметров, правил и весов, который можно применить одной кнопкой.</summary>
public sealed record DomainPreset(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<PresetField> Fields,
    IReadOnlyList<PresetRule> Rules,
    IReadOnlyList<PresetWeight> Weights);

/// <summary>
/// Шаблоны сфер. Движок распределения о них ничего не знает — это просто данные для конструктора:
/// показывают, что тот же код без изменений распределяет обращения банка, колл-центра, склада или магазина.
/// Ключи параметров в разных шаблонах не пересекаются, поэтому подсказки генератору ищутся по ключу.
/// </summary>
public static class DomainPresets
{
    public const string DefaultId = "bank";

    private static readonly string[] OrderTypes = ["ORDER_1", "ORDER_2", "ORDER_3"];
    private static readonly string[] Subjects = ["кредит", "вклад", "карты", "ипотека", "страхование"];
    private static readonly string[] Segments = ["микро", "малый", "средний", "крупный"];
    private static readonly string[] ClientClasses = ["обычный", "VIP"];

    public static readonly DomainPreset Bank = new(
        "bank",
        "Банк: обращения клиентов",
        "Пример из кейса: сумма, тип и тематика заявки, сегмент и категория клиента (VIP). "
        + "Исполнитель подходит, если сумма в его полномочиях и он работает с такими тематиками и клиентами.",
        [
            new(FieldOwner.Order, "sum", "Сумма заявки", FieldType.Number, [], new(Min: 5_000, Max: 5_000_000, LogScale: true)),
            new(FieldOwner.Order, "order_type", "Тип заявки", FieldType.Enum, OrderTypes),
            new(FieldOwner.Order, "subject", "Тематика", FieldType.Enum, Subjects),
            new(FieldOwner.Order, "client_segment", "Сегмент клиента", FieldType.Enum, Segments),
            new(FieldOwner.Order, "client_class", "Категория клиента", FieldType.Enum, ClientClasses, new(Weights: [0.92, 0.08])),
            new(FieldOwner.Executor, "min_sum", "Минимальная сумма", FieldType.Number, [], new(Choices: [0])),
            new(FieldOwner.Executor, "max_sum", "Максимальная сумма", FieldType.Number, [], new(Choices: [500_000, 2_000_000, 10_000_000])),
            new(FieldOwner.Executor, "order_types", "Типы заявок", FieldType.Array, OrderTypes, new(MinItems: 3)),
            new(FieldOwner.Executor, "subjects", "Тематики", FieldType.Array, Subjects, new(MinItems: 2)),
            new(FieldOwner.Executor, "segments", "Сегменты клиентов", FieldType.Array, Segments, new(MinItems: 3)),
            new(FieldOwner.Executor, "client_classes", "Категории клиентов", FieldType.Array, ClientClasses, new(Required: ["обычный"])),
        ],
        [
            new("Сумма в пределах полномочий", "sum", RuleOperator.Between, "min_sum", "max_sum"),
            new("Тип заявки", "order_type", RuleOperator.In, "order_types"),
            new("Тематика", "subject", RuleOperator.In, "subjects"),
            new("Сегмент клиента", "client_segment", RuleOperator.In, "segments"),
            new("Категория клиента", "client_class", RuleOperator.In, "client_classes"),
        ],
        [
            new("client_class", RuleOperator.EqualTo, "VIP", 3),
            new("sum", RuleOperator.GreaterThanOrEqual, 1_000_000, 3),
            new("sum", RuleOperator.GreaterThanOrEqual, 200_000, 2),
        ]);

    private static readonly string[] Channels = ["телефон", "чат", "почта"];
    private static readonly string[] Languages = ["русский", "татарский", "башкирский", "английский"];
    private static readonly string[] Products = ["интернет", "ТВ", "мобильная связь", "облако"];
    private static readonly string[] Tariffs = ["базовый", "бизнес", "премиум"];

    public static readonly DomainPreset Support = new(
        "support",
        "Колл-центр и техподдержка",
        "Обращения абонентов: канал, язык, продукт, тариф и срочность. Оператор подходит, если работает "
        + "в этом канале, говорит на языке клиента и разбирается в продукте; премиум-тарифы — у опытных.",
        [
            new(FieldOwner.Order, "channel", "Канал обращения", FieldType.Enum, Channels, new(Weights: [0.5, 0.35, 0.15])),
            new(FieldOwner.Order, "language", "Язык клиента", FieldType.Enum, Languages, new(Weights: [0.7, 0.15, 0.1, 0.05])),
            new(FieldOwner.Order, "product", "Продукт", FieldType.Enum, Products),
            new(FieldOwner.Order, "tariff", "Тариф", FieldType.Enum, Tariffs, new(Weights: [0.6, 0.3, 0.1])),
            new(FieldOwner.Order, "priority", "Срочность", FieldType.Enum, ["низкая", "обычная", "высокая"], new(Weights: [0.2, 0.65, 0.15])),
            new(FieldOwner.Executor, "channels", "Каналы", FieldType.Array, Channels, new(MinItems: 2)),
            new(FieldOwner.Executor, "languages", "Языки", FieldType.Array, Languages, new(Required: ["русский"], MaxItems: 2)),
            new(FieldOwner.Executor, "products", "Продукты", FieldType.Array, Products, new(MinItems: 2)),
            new(FieldOwner.Executor, "tariffs", "Тарифы", FieldType.Array, Tariffs, new(Required: ["базовый"], MinItems: 2)),
        ],
        [
            new("Канал", "channel", RuleOperator.In, "channels"),
            new("Язык клиента", "language", RuleOperator.In, "languages"),
            new("Продукт", "product", RuleOperator.In, "products"),
            new("Тариф", "tariff", RuleOperator.In, "tariffs"),
        ],
        [
            new("priority", RuleOperator.EqualTo, "высокая", 3),
            new("tariff", RuleOperator.EqualTo, "премиум", 2),
            new("channel", RuleOperator.EqualTo, "телефон", 1.5m),
        ]);

    private static readonly string[] Regions = ["Уфа", "Казань", "Самара", "Пермь"];
    private static readonly string[] CargoTypes = ["обычный", "хрупкий", "опасный", "рефрижератор"];

    public static readonly DomainPreset Logistics = new(
        "logistics",
        "Логистика и склад",
        "Заказы на сборку и доставку: регион, вес и тип груза, срочность. Сотрудник подходит, если обслуживает "
        + "регион, может работать с таким грузом и его допуск по весу не меньше веса груза.",
        [
            new(FieldOwner.Order, "region", "Регион", FieldType.Enum, Regions, new(Weights: [0.4, 0.3, 0.2, 0.1])),
            new(FieldOwner.Order, "cargo_weight", "Вес груза, кг", FieldType.Number, [], new(Min: 1, Max: 2_000, LogScale: true)),
            new(FieldOwner.Order, "cargo_type", "Тип груза", FieldType.Enum, CargoTypes, new(Weights: [0.7, 0.15, 0.05, 0.1])),
            new(FieldOwner.Order, "urgency", "Срочность", FieldType.Enum, ["обычная", "экспресс"], new(Weights: [0.8, 0.2])),
            new(FieldOwner.Executor, "regions", "Регионы", FieldType.Array, Regions, new(MinItems: 1, MaxItems: 3)),
            new(FieldOwner.Executor, "max_cargo_weight", "Допуск по весу, кг", FieldType.Number, [], new(Choices: [300, 1_000, 5_000])),
            new(FieldOwner.Executor, "cargo_types", "Типы груза", FieldType.Array, CargoTypes, new(Required: ["обычный"], MinItems: 2)),
        ],
        [
            new("Регион", "region", RuleOperator.In, "regions"),
            new("Вес в пределах допуска", "cargo_weight", RuleOperator.LessThanOrEqual, "max_cargo_weight"),
            new("Тип груза", "cargo_type", RuleOperator.In, "cargo_types"),
        ],
        [
            new("cargo_type", RuleOperator.EqualTo, "опасный", 3),
            new("urgency", RuleOperator.EqualTo, "экспресс", 2),
            new("cargo_weight", RuleOperator.GreaterThanOrEqual, 500, 2),
        ]);

    private static readonly string[] RequestTypes = ["возврат", "претензия", "обмен", "вопрос"];
    private static readonly string[] Categories = ["электроника", "одежда", "дом", "детские товары"];
    private static readonly string[] Loyalty = ["новый", "постоянный", "золотой"];

    public static readonly DomainPreset Ecommerce = new(
        "ecommerce",
        "Интернет-магазин: возвраты и претензии",
        "Обращения покупателей: тип, категория товара, сумма заказа и уровень лояльности. Менеджер подходит, "
        + "если ведёт такую категорию и тип обращений, сумма в его полномочиях, а золотых клиентов ведут опытные.",
        [
            new(FieldOwner.Order, "request_type", "Тип обращения", FieldType.Enum, RequestTypes, new(Weights: [0.35, 0.2, 0.15, 0.3])),
            new(FieldOwner.Order, "category", "Категория товара", FieldType.Enum, Categories),
            new(FieldOwner.Order, "amount", "Сумма заказа", FieldType.Number, [], new(Min: 300, Max: 300_000, LogScale: true)),
            new(FieldOwner.Order, "loyalty", "Лояльность", FieldType.Enum, Loyalty, new(Weights: [0.3, 0.6, 0.1])),
            new(FieldOwner.Executor, "request_types", "Типы обращений", FieldType.Array, RequestTypes, new(MinItems: 2)),
            new(FieldOwner.Executor, "categories", "Категории", FieldType.Array, Categories, new(MinItems: 2)),
            new(FieldOwner.Executor, "max_amount", "Предельная сумма", FieldType.Number, [], new(Choices: [30_000, 100_000, 500_000])),
            new(FieldOwner.Executor, "loyalty_levels", "Уровни клиентов", FieldType.Array, Loyalty, new(Required: ["новый", "постоянный"])),
        ],
        [
            new("Тип обращения", "request_type", RuleOperator.In, "request_types"),
            new("Категория товара", "category", RuleOperator.In, "categories"),
            new("Сумма в пределах полномочий", "amount", RuleOperator.LessThanOrEqual, "max_amount"),
            new("Лояльность клиента", "loyalty", RuleOperator.In, "loyalty_levels"),
        ],
        [
            new("request_type", RuleOperator.EqualTo, "претензия", 3),
            new("loyalty", RuleOperator.EqualTo, "золотой", 2),
            new("amount", RuleOperator.GreaterThanOrEqual, 50_000, 2),
        ]);

    public static readonly IReadOnlyList<DomainPreset> All = [Bank, Support, Logistics, Ecommerce];

    /// <summary>Имена для демо-исполнителей.</summary>
    public static readonly string[] ExecutorNames =
    [
        "Иванов И.", "Петрова А.", "Сидоров К.", "Галиева Р.", "Хасанов Т.", "Кузнецова Е.", "Смирнов Д.",
        "Юсупова Л.", "Абдуллин Р.", "Морозова О.", "Валиев А.", "Никитина М.", "Фёдоров С.", "Ахметова З.",
        "Попов В.", "Гарипова Э.", "Соколов П.", "Лебедева Н.", "Шарипов И.", "Козлова Ю.",
    ];

    public static DomainPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    public static GeneratorHint? Hint(FieldOwner owner, string key) =>
        All.SelectMany(p => p.Fields).FirstOrDefault(f => f.Owner == owner && f.Key == key)?.Hint;

    /// <summary>Сущности для базы: параметры, правила и правила веса шаблона.</summary>
    public static (List<FieldDefinition> Fields, List<Rule> Rules, List<WeightRule> WeightRules) Build(
        DomainPreset preset, DateTimeOffset now)
    {
        var fields = preset.Fields
            .Select(f => new FieldDefinition { Owner = f.Owner, Key = f.Key, Label = f.Label, Type = f.Type, Options = f.Options })
            .ToList();
        var rules = preset.Rules.Select((r, i) => new Rule
        {
            Name = r.Name,
            Priority = (i + 1) * 10,
            OrderField = r.OrderField,
            Operator = r.Operator,
            Target = RuleTarget.ExecutorField,
            ExecutorField = r.ExecutorField,
            ExecutorFieldUpper = r.ExecutorFieldUpper,
            UpdatedAt = now,
        }).ToList();
        var weightRules = preset.Weights.Select((w, i) => new WeightRule
        {
            Priority = (i + 1) * 10,
            OrderField = w.OrderField,
            Operator = w.Operator,
            ValueJson = JsonSerializer.Serialize(w.Value),
            Weight = w.Weight,
        }).ToList();
        return (fields, rules, weightRules);
    }
}
