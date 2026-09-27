using System.Text.Json.Serialization;

namespace ExecutorBalancer.Domain;

public enum OrderStatus
{
    Processed,
    Await,
    Accept,
    Reject,
}

public enum FieldOwner
{
    Order,
    Executor,
}

public enum FieldType
{
    String,
    Number,
    Boolean,
    Enum,
    Array,
}

public enum RuleOperator
{
    [JsonStringEnumMemberName("equals")] EqualTo,
    [JsonStringEnumMemberName("notEquals")] NotEqualTo,
    [JsonStringEnumMemberName("greaterThan")] GreaterThan,
    [JsonStringEnumMemberName("greaterThanOrEqual")] GreaterThanOrEqual,
    [JsonStringEnumMemberName("lessThan")] LessThan,
    [JsonStringEnumMemberName("lessThanOrEqual")] LessThanOrEqual,
    [JsonStringEnumMemberName("in")] In,
    [JsonStringEnumMemberName("notIn")] NotIn,
    [JsonStringEnumMemberName("contains")] Contains,
    [JsonStringEnumMemberName("between")] Between,
}

public enum RuleTarget
{
    ExecutorField,
    Constant,
}

public enum AssignmentKind
{
    Primary,
    Parent,
    Secondary,
    Reassign,

    /// <summary>Сверх нормы: суточный лимит у всех подходящих исчерпан, заявку берёт доброволец.</summary>
    Extra,
}
