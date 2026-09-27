using System.Text.Json;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Tests;

/// <summary>Мастер новой сферы и разбор «почему сотруднику подходит заявка».</summary>
public class SphereTests : IAsyncLifetime
{
    private static readonly SphereInput Clinic = new("Клиника: запись к врачу",
    [
        new("Специальность", FieldType.Enum, ["терапевт", "хирург", "лор"], SphereMatch.Skills, "Специальности", null, null),
        new("Возраст пациента", FieldType.Number, null, SphereMatch.Max, null, BalancerFixture.Json(65), 2m),
        new("Срочно", FieldType.Boolean, null, SphereMatch.None, null, null, 3m),
    ]);

    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<DepartmentView> CreateClinic() =>
        _f.Departments(d => d.CreateAsync(new DepartmentInput("Поликлиника", null, null, Clinic), CancellationToken.None));

    [Fact]
    public async Task WizardBuildsFieldsRulesAndWeights()
    {
        var clinic = await CreateClinic();
        Assert.Equal("Клиника: запись к врачу", clinic.PresetTitle);

        var config = await _f.Config(c => c.GetAsync(clinic.Id, CancellationToken.None));
        Assert.Contains(config.Fields, f => f is { Owner: FieldOwner.Order, Key: "spetsialnost", Type: FieldType.Enum });
        Assert.Contains(config.Fields, f => f is { Owner: FieldOwner.Executor, Key: "spetsialnosti", Type: FieldType.Array });
        Assert.Contains(config.Fields, f => f is { Owner: FieldOwner.Executor, Label: "Возраст пациента: максимум" });
        Assert.Equal(2, config.Rules.Count);
        Assert.All(config.Rules, r => Assert.Null(r.Error));
        Assert.Equal(2, config.WeightRules.Count);
        Assert.All(config.WeightRules, r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task WizardSphereDistributesOrders()
    {
        var clinic = await CreateClinic();
        await _f.Run(async b =>
        {
            await b.UpsertExecutorAsync(clinic.Id, new IncomingExecutor(501, "Терапевт", true, null, 1m,
                BalancerFixture.Attributes(new { spetsialnosti = new[] { "терапевт" }, vozrast_patsienta_maksimum = 120 })), CancellationToken.None);
            await b.UpsertExecutorAsync(clinic.Id, new IncomingExecutor(502, "Лор детский", true, null, 1m,
                BalancerFixture.Attributes(new { spetsialnosti = new[] { "лор" }, vozrast_patsienta_maksimum = 17 })), CancellationToken.None);
            return true;
        });

        var adult = await _f.Run(b => b.ReceiveAsync(clinic.Id, new IncomingOrder(9001, null, OrderStatus.Processed,
            BalancerFixture.Attributes(new { spetsialnost = "лор", vozrast_patsienta = 40, srochno = true })), CancellationToken.None));
        Assert.Equal(BalanceOutcome.Pending, adult.Outcome); // детскому лору взрослый не подходит

        var child = await _f.Run(b => b.ReceiveAsync(clinic.Id, new IncomingOrder(9002, null, OrderStatus.Processed,
            BalancerFixture.Attributes(new { spetsialnost = "лор", vozrast_patsienta = 7 })), CancellationToken.None));
        Assert.Equal(502, child.ExecutorId);

        // срочная заявка весит 3 (первое сработавшее правило веса по приоритету — «возраст от 65» не выполнено)
        var check = await _f.Run(b => b.CheckExecutorAsync(clinic.Id, 502,
            BalancerFixture.Attributes(new { spetsialnost = "лор", vozrast_patsienta = 40, srochno = true }), CancellationToken.None));
        Assert.NotNull(check);
        Assert.False(check.CanTake);
        Assert.Equal(3m, check.OrderWeight);
        var failed = Assert.Single(check.Rules, r => !r.Passed);
        Assert.Equal("Возраст пациента", failed.Rule);
        Assert.Equal("40", failed.OrderValue);
        Assert.Equal("17", failed.Expected);
    }

    [Theory]
    [InlineData("", FieldType.Enum, SphereMatch.Skills)]          // без названия
    [InlineData("Регион", FieldType.Boolean, SphereMatch.Skills)]  // да/нет не сопоставляется со списком
    [InlineData("Сумма", FieldType.Enum, SphereMatch.Max)]         // максимум — только у чисел
    public async Task WizardRejectsInvalidSphere(string label, FieldType type, SphereMatch match)
    {
        var sphere = new SphereInput("Своя", [new(label, type, ["a", "b"], match, null, null, null)]);
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            _f.Departments(d => d.CreateAsync(new DepartmentInput("Отдел X", null, null, sphere), CancellationToken.None)));
        Assert.DoesNotContain(await _f.Departments(d => d.ListAsync(CancellationToken.None)), d => d.Name == "Отдел X");
    }

    [Fact]
    public void KeysAreTransliteratedAndUnique()
    {
        var taken = new HashSet<string>();
        Assert.Equal("yazyk_klienta", SphereBuilder.Key("Язык клиента", "p", taken));
        Assert.Equal("yazyk_klienta_2", SphereBuilder.Key("Язык  клиента!", "p", taken));
        Assert.Equal("p_1s", SphereBuilder.Key("1С", "p", taken)); // «С» кириллическая
        Assert.Equal("p", SphereBuilder.Key("???", "p", taken));
    }

    [Fact]
    public async Task CheckExplainsEveryRuleForBankExecutor()
    {
        await _f.AddExecutor(1, subjects: ["вклад"]);
        var check = await _f.Run(b => b.CheckExecutorAsync(D, 1, BalancerFixture.Attributes(BalancerFixture.DefaultOrder()),
            CancellationToken.None));

        Assert.NotNull(check);
        Assert.False(check.CanTake);
        var subject = Assert.Single(check.Rules, r => !r.Passed);
        Assert.Equal("кредит", subject.OrderValue);
        Assert.Contains("вклад", subject.Expected);
        Assert.Null(await _f.Run(b => b.CheckExecutorAsync(D, 777, BalancerFixture.Attributes(new { }), CancellationToken.None)));
        _ = JsonSerializer.Serialize(check); // уходит в интерфейс как есть
    }
}
