using System.Text.Json;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Infrastructure.Persistence;

/// <summary>Стартовые параметры и правила для примера из кейса. Пишутся только в пустую базу.</summary>
internal static class DefaultConfiguration
{
    private static readonly string[] OrderTypes = ["ORDER_1", "ORDER_2", "ORDER_3"];
    private static readonly string[] Subjects = ["credit", "deposit", "cards", "mortgage", "insurance"];
    private static readonly string[] Segments = ["micro", "small", "medium", "large"];
    private static readonly string[] ClientClasses = ["standard", "vip"];

    public static async Task SeedAsync(BalancerDbContext db, CancellationToken cancellationToken)
    {
        if (await db.FieldDefinitions.AnyAsync(cancellationToken))
        {
            return;
        }

        db.FieldDefinitions.AddRange(
            Field(FieldOwner.Order, "sum", "Сумма заявки", FieldType.Number),
            Field(FieldOwner.Order, "order_type", "Тип заявки", FieldType.Enum, OrderTypes),
            Field(FieldOwner.Order, "subject", "Тематика", FieldType.Enum, Subjects),
            Field(FieldOwner.Order, "client_segment", "Сегмент клиента", FieldType.Enum, Segments),
            Field(FieldOwner.Order, "client_class", "Категория клиента", FieldType.Enum, ClientClasses),
            Field(FieldOwner.Executor, "min_sum", "Минимальная сумма", FieldType.Number),
            Field(FieldOwner.Executor, "max_sum", "Максимальная сумма", FieldType.Number),
            Field(FieldOwner.Executor, "order_types", "Типы заявок", FieldType.Array, OrderTypes),
            Field(FieldOwner.Executor, "subjects", "Тематики", FieldType.Array, Subjects),
            Field(FieldOwner.Executor, "segments", "Сегменты клиентов", FieldType.Array, Segments),
            Field(FieldOwner.Executor, "client_classes", "Категории клиентов", FieldType.Array, ClientClasses));

        var now = DateTimeOffset.UtcNow;
        db.Rules.AddRange(
            new Rule
            {
                Name = "Сумма в пределах полномочий", Priority = 10, OrderField = "sum",
                Operator = RuleOperator.Between, Target = RuleTarget.ExecutorField,
                ExecutorField = "min_sum", ExecutorFieldUpper = "max_sum", UpdatedAt = now,
            },
            InList("Тип заявки", 20, "order_type", "order_types", now),
            InList("Тематика", 30, "subject", "subjects", now),
            InList("Сегмент клиента", 40, "client_segment", "segments", now),
            InList("Категория клиента", 50, "client_class", "client_classes", now));

        db.WeightRules.AddRange(
            new WeightRule { Priority = 10, OrderField = "client_class", Operator = RuleOperator.EqualTo, ValueJson = Json("vip"), Weight = 3 },
            new WeightRule { Priority = 20, OrderField = "sum", Operator = RuleOperator.GreaterThanOrEqual, ValueJson = Json(1_000_000), Weight = 3 },
            new WeightRule { Priority = 30, OrderField = "sum", Operator = RuleOperator.GreaterThanOrEqual, ValueJson = Json(200_000), Weight = 2 });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static FieldDefinition Field(FieldOwner owner, string key, string label, FieldType type,
        string[]? options = null) =>
        new() { Owner = owner, Key = key, Label = label, Type = type, Options = options ?? [] };

    private static Rule InList(string name, int priority, string orderField, string executorField, DateTimeOffset now) =>
        new()
        {
            Name = name, Priority = priority, OrderField = orderField, Operator = RuleOperator.In,
            Target = RuleTarget.ExecutorField, ExecutorField = executorField, UpdatedAt = now,
        };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
