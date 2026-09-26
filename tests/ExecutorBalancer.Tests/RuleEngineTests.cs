using System.Text.Json;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Tests;

public class RuleEngineTests
{
    private static readonly FieldCatalog Catalog = new(
    [
        Field(FieldOwner.Order, "sum", FieldType.Number),
        Field(FieldOwner.Order, "region", FieldType.Enum, "ufa", "kazan"),
        Field(FieldOwner.Order, "vip", FieldType.Boolean),
        Field(FieldOwner.Order, "tags", FieldType.Array),
        Field(FieldOwner.Order, "comment", FieldType.String),
        Field(FieldOwner.Executor, "max_sum", FieldType.Number),
        Field(FieldOwner.Executor, "min_sum", FieldType.Number),
        Field(FieldOwner.Executor, "regions", FieldType.Array, "ufa", "kazan"),
        Field(FieldOwner.Executor, "vip_access", FieldType.Boolean),
    ]);

    [Fact]
    public void ComparesOrderFieldWithExecutorField()
    {
        var rule = Compile("sum", RuleOperator.LessThanOrEqual, executorField: "max_sum");

        Assert.True(rule.Check(Order(new { sum = 100 }), Executor(new { max_sum = 100 })).Passed);
        var outcome = rule.Check(Order(new { sum = 101 }), Executor(new { max_sum = 100 }));
        Assert.False(outcome.Passed);
        Assert.Contains("101", outcome.Reason);
    }

    [Fact]
    public void InChecksMembershipInExecutorList()
    {
        var rule = Compile("region", RuleOperator.In, executorField: "regions");

        Assert.True(rule.Check(Order(new { region = "ufa" }), Executor(new { regions = new[] { "ufa" } })).Passed);
        Assert.False(rule.Check(Order(new { region = "kazan" }), Executor(new { regions = new[] { "ufa" } })).Passed);
    }

    [Fact]
    public void EmptyValueDoesNotRestrictUnlessRuleIsStrict()
    {
        var lenient = Compile("sum", RuleOperator.LessThanOrEqual, executorField: "max_sum");
        var strict = Compile("sum", RuleOperator.LessThanOrEqual, executorField: "max_sum", strict: true);

        Assert.True(lenient.Check(Order(new { sum = 5 }), Executor(new { })).Passed);
        Assert.False(strict.Check(Order(new { sum = 5 }), Executor(new { })).Passed);
    }

    [Fact]
    public void BetweenWithExecutorBoundsTreatsMissingBoundAsOpen()
    {
        var rule = Compile("sum", RuleOperator.Between, executorField: "min_sum", upper: "max_sum");

        Assert.True(rule.Check(Order(new { sum = 500 }), Executor(new { min_sum = 100, max_sum = 1000 })).Passed);
        Assert.False(rule.Check(Order(new { sum = 50 }), Executor(new { min_sum = 100, max_sum = 1000 })).Passed);
        Assert.True(rule.Check(Order(new { sum = 5_000_000 }), Executor(new { min_sum = 100 })).Passed);
    }

    [Fact]
    public void BetweenWithConstantRange()
    {
        var rule = Compile("sum", RuleOperator.Between, value: new[] { 10, 20 });

        Assert.True(rule.Check(Order(new { sum = 20 }), Executor(new { })).Passed);
        Assert.False(rule.Check(Order(new { sum = 21 }), Executor(new { })).Passed);
    }

    [Fact]
    public void ContainsWorksForArraysAndText()
    {
        Assert.True(Compile("tags", RuleOperator.Contains, value: "urgent")
            .Check(Order(new { tags = new[] { "urgent", "new" } }), Executor(new { })).Passed);
        Assert.True(Compile("comment", RuleOperator.Contains, value: "СРОЧНО")
            .Check(Order(new { comment = "очень срочно" }), Executor(new { })).Passed);
    }

    [Fact]
    public void NumbersAreComparedIndependentlyOfFormat()
    {
        var rule = Compile("sum", RuleOperator.EqualTo, value: 5);

        Assert.True(rule.Check(Order(new { sum = 5.00m }), Executor(new { })).Passed);
    }

    [Theory]
    [InlineData("sum", RuleOperator.In, "max_sum")]
    [InlineData("region", RuleOperator.GreaterThan, "max_sum")]
    [InlineData("vip", RuleOperator.EqualTo, "regions")]
    [InlineData("sum", RuleOperator.LessThan, "unknown")]
    [InlineData("unknown", RuleOperator.EqualTo, "max_sum")]
    public void IncompatibleRulesAreRejected(string orderField, RuleOperator op, string executorField)
    {
        Assert.Throws<RuleValidationException>(() => Compile(orderField, op, executorField: executorField));
    }

    [Fact]
    public void ConstantOutsideDictionaryIsRejected()
    {
        Assert.Throws<RuleValidationException>(() => Compile("region", RuleOperator.In, value: new[] { "moscow" }));
    }

    [Fact]
    public void InvalidOrderValuesAreReported()
    {
        var errors = new Dictionary<string, string[]>();
        Catalog.Parse(FieldOwner.Order, Raw(new { sum = "много", region = "moscow", vip = "да" }), errors);

        Assert.Equal(3, errors.Count);
    }

    private static CompiledRule Compile(string orderField, RuleOperator op, string? executorField = null,
        string? upper = null, object? value = null, bool strict = false) =>
        RuleCompiler.Compile(new Rule
        {
            Id = 1,
            Name = "правило",
            OrderField = orderField,
            Operator = op,
            Target = executorField is null ? RuleTarget.Constant : RuleTarget.ExecutorField,
            ExecutorField = executorField,
            ExecutorFieldUpper = upper,
            ValueJson = value is null ? null : JsonSerializer.Serialize(value),
            IsStrict = strict,
        }, Catalog);

    private static IReadOnlyDictionary<string, FieldValue> Order(object value) =>
        Catalog.Parse(FieldOwner.Order, Raw(value), null);

    private static IReadOnlyDictionary<string, FieldValue> Executor(object value) =>
        Catalog.Parse(FieldOwner.Executor, Raw(value), null);

    private static Dictionary<string, JsonElement> Raw(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static FieldDefinition Field(FieldOwner owner, string key, FieldType type, params string[] options) =>
        new() { Owner = owner, Key = key, Label = key, Type = type, Options = options };
}
