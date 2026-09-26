using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Application.Rules;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

public class PresetTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    public static TheoryData<string> PresetIds => new(DomainPresets.All.Select(p => p.Id));

    [Theory]
    [MemberData(nameof(PresetIds))]
    public void EveryPresetRuleCompiles(string id)
    {
        var (fields, rules, weightRules) = DomainPresets.Build(DomainPresets.Find(id)!, DateTimeOffset.UtcNow);
        var catalog = new FieldCatalog(fields);

        Assert.All(rules, r => RuleCompiler.Compile(r, catalog));
        Assert.All(weightRules, r => RuleCompiler.CompileWeight(r, catalog));
        Assert.All(fields, f => Assert.True(FieldCatalog.IsValidKey(f.Key), f.Key));
    }

    [Fact]
    public void PresetKeysDoNotOverlapSoHintsAreUnambiguous()
    {
        var keys = DomainPresets.All.SelectMany(p => p.Fields.Select(f => (f.Owner, f.Key))).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task BankIsTheStartingConfiguration()
    {
        var presets = await _f.Config(c => c.GetPresetsAsync(CancellationToken.None));
        Assert.Equal("bank", Assert.Single(presets, p => p.IsCurrent).Id);
    }

    [Fact]
    public async Task ApplyingPresetReplacesConfigurationAndIsAudited()
    {
        await _f.Config(async c =>
        {
            await c.ApplyPresetAsync("logistics", CancellationToken.None);
            return true;
        });

        var view = await _f.Config(c => c.GetAsync(CancellationToken.None));
        Assert.Contains(view.Fields, f => f.Key == "cargo_weight");
        Assert.DoesNotContain(view.Fields, f => f.Key == "sum");
        Assert.All(view.Rules, r => Assert.Null(r.Error));
        var audit = await _f.Config(c => c.GetAuditAsync(null, 1, CancellationToken.None));
        Assert.Equal("preset_applied", audit[0].Action);
        Assert.Equal("logistics", audit[0].EntityId);
    }

    [Fact]
    public async Task SameEngineDistributesLogisticsOrders()
    {
        await _f.Config(async c =>
        {
            await c.ApplyPresetAsync("logistics", CancellationToken.None);
            return true;
        });
        await _f.AddExecutor(1, extra: new { regions = new[] { "Уфа" }, max_cargo_weight = 300, cargo_types = new[] { "обычный" } });
        await _f.AddExecutor(2, extra: new { regions = new[] { "Уфа", "Казань" }, max_cargo_weight = 5000, cargo_types = new[] { "обычный", "опасный" } });

        var heavy = await _f.Receive(1, attributes: new { region = "Уфа", cargo_weight = 800, cargo_type = "обычный", urgency = "обычная" });
        var dangerous = await _f.Receive(2, attributes: new { region = "Казань", cargo_weight = 10, cargo_type = "опасный", urgency = "экспресс" });

        Assert.Equal(2, heavy.ExecutorId);
        Assert.Equal(2, dangerous.ExecutorId);
        var weight = await _f.Query(db => db.Orders.Where(o => o.Id == 2).Select(o => o.Weight).FirstAsync());
        Assert.Equal(3m, weight);
    }

    [Fact]
    public async Task UnknownPresetIsRejected()
    {
        await Assert.ThrowsAsync<InvalidInputException>(() => _f.Config(async c =>
        {
            await c.ApplyPresetAsync("../etc", CancellationToken.None);
            return true;
        }));
    }
}
