using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

public class ConfigurationServiceTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<FieldView> AddLanguageFields() =>
        _f.Config(async c =>
        {
            await c.CreateFieldAsync(new FieldInput(FieldOwner.Order, "language", "Язык обращения", FieldType.Enum,
                ["ru", "tt", "en"]), CancellationToken.None);
            return await c.CreateFieldAsync(new FieldInput(FieldOwner.Executor, "languages", "Языки", FieldType.Array,
                ["ru", "tt", "en"]), CancellationToken.None);
        });

    private static RuleInput LanguageRule(bool strict = false) => new("Язык", true, 60, "language", RuleOperator.In,
        RuleTarget.ExecutorField, "languages", null, null, strict);

    [Fact]
    public async Task NewFieldAndRuleApplyToTheNextOrderWithoutRestart()
    {
        await _f.AddExecutor(1, qualification: 3m, extra: new { languages = new[] { "ru" } });
        await _f.AddExecutor(2, qualification: 1m, extra: new { languages = new[] { "ru", "en" } });
        Assert.Equal(1, (await _f.Receive(1)).ExecutorId);
        var preview = await _f.Run(b => b.PreviewAsync(null, BalancerFixture.Attributes(new
        {
            sum = 50_000, order_type = "ORDER_1", subject = "кредит", client_segment = "малый",
            client_class = "обычный", language = "en",
        }), CancellationToken.None));
        Assert.Equal(1, preview.ChosenExecutorId); // пока правила нет, язык не важен: у первого score 2/3 против 1

        // значения языков АИС прислала раньше, чем появился параметр: они хранятся и начинают учитываться сразу
        await AddLanguageFields();
        await _f.Config(c => c.CreateRuleAsync(LanguageRule(), CancellationToken.None));

        // по score снова выиграл бы первый, но заявка на английском, а он говорит только по-русски
        var result = await _f.Receive(2, attributes: new
        {
            sum = 50_000, order_type = "ORDER_1", subject = "кредит", client_segment = "малый",
            client_class = "обычный", language = "en",
        });
        Assert.Equal(2, result.ExecutorId);
    }

    [Fact]
    public async Task IncompatibleOperatorIsRejected()
    {
        await AddLanguageFields();
        var error = await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(c => c.CreateRuleAsync(
            LanguageRule() with { Operator = RuleOperator.GreaterThan }, CancellationToken.None)));
        Assert.Contains("больше", error.Errors["rule"][0]);
    }

    [Fact]
    public async Task ConstantOutsideDictionaryIsRejected()
    {
        await AddLanguageFields();
        await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(c => c.CreateRuleAsync(
            new RuleInput("Только немецкий", true, 1, "language", RuleOperator.EqualTo, RuleTarget.Constant, null, null,
                BalancerFixture.Json("de"), false), CancellationToken.None)));
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("1abc")]
    [InlineData("drop;table")]
    [InlineData("")]
    public async Task InvalidFieldKeyIsRejected(string key)
    {
        var error = await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(c => c.CreateFieldAsync(
            new FieldInput(FieldOwner.Order, key, "x", FieldType.String, null), CancellationToken.None)));
        Assert.True(error.Errors.ContainsKey("key"));
    }

    [Fact]
    public async Task UndefinedEnumValuesAreRejected()
    {
        var error = await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(c => c.CreateFieldAsync(
            new FieldInput((FieldOwner)7, "zz", "x", (FieldType)99, null), CancellationToken.None)));
        Assert.True(error.Errors.ContainsKey("owner"));
        Assert.True(error.Errors.ContainsKey("type"));
    }

    [Fact]
    public async Task DuplicateFieldKeyIsConflict()
    {
        await AddLanguageFields();
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => _f.Config(c => c.CreateFieldAsync(
            new FieldInput(FieldOwner.Order, "language", "Другой", FieldType.String, null), CancellationToken.None)));
    }

    [Fact]
    public async Task FieldUsedByRuleCannotBeDeleted()
    {
        var executorField = await AddLanguageFields();
        await _f.Config(c => c.CreateRuleAsync(LanguageRule(), CancellationToken.None));

        var error = await Assert.ThrowsAsync<ConfigurationConflictException>(() =>
            _f.Config(c => c.DeleteFieldAsync(executorField.Id, CancellationToken.None)));
        Assert.Contains("«Язык»", error.Message);
    }

    [Fact]
    public async Task FieldTypeCannotBeChanged()
    {
        var field = await AddLanguageFields();
        var error = await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(c => c.UpdateFieldAsync(field.Id,
            new FieldInput(null, null, "Языки", FieldType.String, null), CancellationToken.None)));
        Assert.True(error.Errors.ContainsKey("type"));
    }

    [Fact]
    public async Task OptionUsedByExecutorCannotBeRemoved()
    {
        var field = await AddLanguageFields();
        await _f.AddExecutor(1, extra: new { languages = new[] { "tt" } });

        await Assert.ThrowsAsync<ConfigurationConflictException>(() => _f.Config(c => c.UpdateFieldAsync(field.Id,
            new FieldInput(null, null, "Языки", null, ["ru", "en"]), CancellationToken.None)));

        var updated = await _f.Config(c => c.UpdateFieldAsync(field.Id,
            new FieldInput(null, null, "Языки исполнителя", null, ["ru", "tt", "en", "ba"]), CancellationToken.None));
        Assert.Equal(4, updated!.Options.Length);
    }

    [Fact]
    public async Task WeightRuleChangesWeightOfNewOrders()
    {
        await _f.AddExecutor(1);
        await _f.Config(c => c.CreateWeightRuleAsync(new WeightRuleInput(true, 1, "subject", RuleOperator.EqualTo,
            BalancerFixture.Json("ипотека"), 5m), CancellationToken.None));

        await _f.Receive(1, attributes: BalancerFixture.DefaultOrder(subject: "ипотека"));

        var weight = await _f.Query(db => db.Orders.Where(o => o.Id == 1).Select(o => o.Weight).FirstAsync());
        Assert.Equal(5m, weight);
    }

    [Fact]
    public async Task EveryChangeIsAudited()
    {
        var field = await AddLanguageFields();
        var rule = await _f.Config(c => c.CreateRuleAsync(LanguageRule(), CancellationToken.None));
        await _f.Config(c => c.UpdateRuleAsync(rule.Id, LanguageRule(strict: true), CancellationToken.None));
        await _f.Config(c => c.DeleteRuleAsync(rule.Id, CancellationToken.None));
        await _f.Config(c => c.DeleteFieldAsync(field.Id, CancellationToken.None));

        var audit = await _f.Config(c => c.GetAuditAsync(null, 10, CancellationToken.None));
        Assert.Equal(["field_deleted", "rule_deleted", "rule_updated", "rule_created", "field_created", "field_created"],
            audit.Select(a => a.Action));
        var update = audit.Single(a => a.Action == "rule_updated");
        Assert.False(update.Data!.Value.GetProperty("before").GetProperty("isStrict").GetBoolean());
        Assert.True(update.Data!.Value.GetProperty("after").GetProperty("isStrict").GetBoolean());
    }

    [Fact]
    public async Task ConfigurationViewDescribesRulesInPlainLanguage()
    {
        var view = await _f.Config(c => c.GetAsync(CancellationToken.None));

        Assert.Contains(view.Rules, r => r.Text == "Сумма заявки в диапазоне [Минимальная сумма; Максимальная сумма] исполнителя");
        Assert.All(view.Rules, r => Assert.Null(r.Error));
        Assert.Contains(view.WeightRules, r => r.Text == "Категория клиента равно VIP");
        Assert.Equal(3, view.Fields.Single(f => f.Key == "sum").UsedByRules);
    }
}
