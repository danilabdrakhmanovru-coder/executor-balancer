using System.Text.Json;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Executors;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>«Умеющий всё» сотрудник для демонстрации двух одинаковых: подходит под любую заявку в любой сфере.</summary>
public class AllCapableTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Theory]
    [InlineData("bank")]
    [InlineData("support")]
    [InlineData("logistics")]
    [InlineData("ecommerce")]
    public async Task TakesTheMostDemandingOrderOfEverySphere(string preset)
    {
        var department = await _f.Departments(d => d.CreateAsync(
            new DepartmentInput($"Два сотрудника {preset}", $"duo-{preset}", preset), CancellationToken.None));
        var fields = await _f.Query(db => db.FieldDefinitions.AsNoTracking().Where(f => f.DepartmentId == department.Id).ToListAsync());
        var rules = await _f.Query(db => db.Rules.AsNoTracking().Where(r => r.DepartmentId == department.Id).ToListAsync());
        var skills = AllCapable.Skills(fields, rules);

        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(department.Id, new IncomingExecutor(7001, "Умеет всё", true, null, 1m, skills),
                CancellationToken.None);
            return true;
        });

        // заявка с последним значением каждого списка и крупными числами — самая «неудобная»
        var order = fields.Where(f => f.Owner == FieldOwner.Order).ToDictionary(f => f.Key, f => f.Type switch
        {
            FieldType.Number => JsonSerializer.SerializeToElement(4_000_000m),
            FieldType.Boolean => JsonSerializer.SerializeToElement(true),
            FieldType.Array => JsonSerializer.SerializeToElement(f.Options),
            _ => JsonSerializer.SerializeToElement(f.Options.LastOrDefault() ?? "x"),
        });
        var result = await _f.Run(b => b.ReceiveAsync(department.Id,
            new IncomingOrder(90_000 + department.Id, null, OrderStatus.Processed, order), CancellationToken.None));

        Assert.Equal(7001, result.ExecutorId);
    }

    [Fact]
    public void BoundsFollowTheRules()
    {
        var fields = new[]
        {
            new FieldDefinition { Owner = FieldOwner.Executor, Key = "min_sum", Type = FieldType.Number },
            new FieldDefinition { Owner = FieldOwner.Executor, Key = "max_sum", Type = FieldType.Number },
            new FieldDefinition { Owner = FieldOwner.Executor, Key = "topics", Type = FieldType.Array, Options = ["a", "b"] },
        };
        var rules = new[]
        {
            new Rule { Operator = RuleOperator.Between, Target = RuleTarget.ExecutorField, ExecutorField = "min_sum", ExecutorFieldUpper = "max_sum" },
        };

        var skills = AllCapable.Skills(fields, rules);

        Assert.Equal(0m, skills["min_sum"].GetDecimal());
        Assert.Equal(AllCapable.Unlimited, skills["max_sum"].GetDecimal());
        Assert.Equal(["a", "b"], skills["topics"].EnumerateArray().Select(v => v.GetString()));
    }
}
