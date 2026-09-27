using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Tests;

/// <summary>
/// Условие кейса: у исполнителя больше 20 параметров, у заявки — больше 15, и они добавляются без изменения кода.
/// Отдел собирается только через конструктор: 16 параметров заявки, 21 параметр исполнителя, 16 правил.
/// </summary>
public class ManyParametersTests : IAsyncLifetime
{
    private const int OrderFields = 16;
    private const int ExecutorFields = 21;
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task SixteenOrderAndTwentyOneExecutorParametersWork()
    {
        var department = await _f.Departments(d => d.CreateAsync(new DepartmentInput("Много параметров", "many", null), CancellationToken.None));
        var id = department.Id;
        string[] options = ["a", "b", "c"];

        for (var i = 1; i <= OrderFields; i++)
        {
            await _f.Config(c => c.CreateFieldAsync(id, new FieldInput(FieldOwner.Order, $"p{i}", $"Параметр {i}", FieldType.Enum, options), CancellationToken.None));
            await _f.Config(c => c.CreateFieldAsync(id, new FieldInput(FieldOwner.Executor, $"s{i}", $"Навык {i}", FieldType.Array, options), CancellationToken.None));
            await _f.Config(c => c.CreateRuleAsync(id, new RuleInput($"Правило {i}", true, i * 10, $"p{i}", RuleOperator.In,
                RuleTarget.ExecutorField, $"s{i}", null, null, IsStrict: true), CancellationToken.None));
        }

        // ещё 5 параметров исполнителя, не участвующих в правилах (как в жизни: не всё влияет на подбор)
        for (var i = OrderFields + 1; i <= ExecutorFields; i++)
        {
            await _f.Config(c => c.CreateFieldAsync(id, new FieldInput(FieldOwner.Executor, $"s{i}", $"Навык {i}", FieldType.Number, []), CancellationToken.None));
        }

        var config = await _f.Config(c => c.GetAsync(id, CancellationToken.None));
        Assert.Equal(OrderFields, config.Fields.Count(f => f.Owner == FieldOwner.Order));
        Assert.Equal(ExecutorFields, config.Fields.Count(f => f.Owner == FieldOwner.Executor));
        Assert.Equal(OrderFields, config.Rules.Count);
        Assert.All(config.Rules, r => Assert.Null(r.Error));

        // исполнитель 1 умеет всё, исполнитель 2 не умеет «c» по последнему параметру
        Dictionary<string, object> Skills(bool full) => Enumerable.Range(1, ExecutorFields).ToDictionary(
            i => $"s{i}",
            i => i > OrderFields ? 1 : (object)(full || i < OrderFields ? options : new[] { "a", "b" }));
        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(id, new IncomingExecutor(901, "Универсал", true, null, 1m,
                BalancerFixture.Attributes(Skills(full: true))), CancellationToken.None);
            await b.UpsertExecutorAsync(id, new IncomingExecutor(902, "Почти универсал", true, null, 1m,
                BalancerFixture.Attributes(Skills(full: false))), CancellationToken.None);
            return true;
        });

        var order = Enumerable.Range(1, OrderFields).ToDictionary(i => $"p{i}", i => i == OrderFields ? "c" : "a");
        var result = await _f.Run(b => b.ReceiveAsync(id, new IncomingOrder(90_001, null, OrderStatus.Processed,
            BalancerFixture.Attributes(order)), CancellationToken.None));

        Assert.Equal(901, result.ExecutorId); // единственный, кто подходит по всем 16 правилам
    }
}
