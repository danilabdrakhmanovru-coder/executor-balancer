using System.Text;
using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>Подписи значений справочника: коды (ORDER_1…3 из модели данных кейса) внутри, названия — на экране.</summary>
public class OptionLabelTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<FieldView> Field(string key) =>
        _f.Config(async c => (await c.GetAsync(D, CancellationToken.None)).Fields.Single(f => f.Key == key));

    [Fact]
    public async Task BankOrderTypesHaveLabelsButKeepCodes()
    {
        var type = await Field("order_type");

        Assert.Equal(["ORDER_1", "ORDER_2", "ORDER_3"], type.Options);
        Assert.Equal(["Консультация", "Оформление", "Претензия"], type.OptionLabels!);
        Assert.Equal("Претензия", new FieldDefinition { Options = type.Options, OptionLabels = type.OptionLabels! }.Show("ORDER_3"));
        Assert.Equal("кредит", new FieldDefinition { Options = ["кредит"] }.Show("кредит")); // без подписи — само значение
    }

    [Fact]
    public async Task RuleExplanationShowsLabels()
    {
        await _f.AddExecutor(1, extra: new { order_types = new[] { "ORDER_1", "ORDER_2" } });
        var order = BalancerFixture.Attributes(new { sum = 50_000, order_type = "ORDER_3", subject = "кредит" });

        var check = await _f.Run(b => b.CheckExecutorAsync(D, 1, order, CancellationToken.None));

        var failed = Assert.Single(check!.Rules, r => !r.Passed);
        Assert.Equal("Претензия", failed.OrderValue);
        Assert.Contains("Консультация", failed.Expected);
        Assert.DoesNotContain("ORDER_", failed.Expected);
    }

    [Fact]
    public async Task LabelsSurviveOptionChangesAndAreValidated()
    {
        var type = await Field("order_type");

        // подписи не переданы — остаются у тех же значений, даже при новом порядке
        var updated = await _f.Config(c => c.UpdateFieldAsync(D, type.Id,
            new FieldInput(null, null, type.Label, null, ["ORDER_3", "ORDER_1", "ORDER_2"]), CancellationToken.None));
        Assert.Equal(["Претензия", "Консультация", "Оформление"], updated!.OptionLabels!);

        var error = await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(c => c.UpdateFieldAsync(D, type.Id,
            new FieldInput(null, null, type.Label, null, type.Options, ["только одна"]), CancellationToken.None)));
        Assert.Contains("optionLabels", error.Errors.Keys);
    }

    [Fact]
    public async Task ImportAcceptsLabelsAndStoresCodes()
    {
        var template = Encoding.UTF8.GetString(await _f.Import(i => i.TemplateAsync(D, CancellationToken.None)));
        Assert.Contains("Консультация", template, StringComparison.Ordinal);

        var file = Encoding.UTF8.GetBytes("""
            ФИО;Типы заявок;Тематики
            Орлова Н. П.;"Консультация, претензия";кредит
            """);
        var result = await _f.Import(i => i.ApplyAsync(D, file, CancellationToken.None));

        Assert.True(result.Applied);
        var stored = await _f.Query(db => db.Executors.AsNoTracking().SingleAsync(e => e.FullName == "Орлова Н. П."));
        Assert.Contains("\"ORDER_1\"", stored.AttributesJson, StringComparison.Ordinal);
        Assert.Contains("\"ORDER_3\"", stored.AttributesJson, StringComparison.Ordinal);
    }
}
