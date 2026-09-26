using AisEmulator.Api.Simulation;

namespace ExecutorBalancer.Tests;

public class SimulationTests
{
    private static readonly Random Seeded = new(7);

    [Fact]
    public void EnumValuesComeFromDictionaryAndFollowWeights()
    {
        var spec = new ValueSpec("client_class", "Enum", ["standard", "vip"], Weights: [0.9, 0.1]);
        var values = Enumerable.Range(0, 2000).Select(_ => spec.Generate(Seeded).GetString()).ToList();

        Assert.All(values, v => Assert.Contains(v, new[] { "standard", "vip" }));
        Assert.InRange(values.Count(v => v == "vip"), 120, 280);
    }

    [Fact]
    public void NumbersStayWithinBounds()
    {
        var spec = new ValueSpec("sum", "Number", Min: 5_000, Max: 5_000_000, LogScale: true);
        var values = Enumerable.Range(0, 1000).Select(_ => spec.Generate(Seeded).GetDecimal()).ToList();

        Assert.All(values, v => Assert.InRange(v, 5_000m, 5_000_000m));
        // логарифмическая шкала: мелких сумм больше, чем крупных
        Assert.True(values.Count(v => v < 100_000) > values.Count(v => v > 1_000_000));
    }

    [Fact]
    public void ArrayKeepsRequiredValuesAndDictionaryOrder()
    {
        var options = new[] { "ru", "tt", "ba", "en" };
        var spec = new ValueSpec("languages", "Array", options, Required: ["ru"], MaxItems: 2);
        for (var i = 0; i < 200; i++)
        {
            var items = spec.Generate(Seeded).EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Contains("ru", items);
            Assert.InRange(items.Length, 1, 2);
            Assert.Equal(items, options.Where(items.Contains));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1_000_000)]
    public void RateOutOfRangeIsRejected(double rate) =>
        Assert.NotNull(new SimulationRequest(rate, []).Validate());

    [Fact]
    public void SpecWithUnknownTypeIsRejected() =>
        Assert.NotNull(new SimulationRequest(100, [new ValueSpec("x", "Script")]).Validate());

    [Fact]
    public void SeedBuildsRequestedExecutors()
    {
        var request = new SeedExecutorsRequest(3, [new ValueSpec("subjects", "Array", ["credit", "cards"], MinItems: 1)],
            ["Иванов И.", "Петрова А."], [null, 60], [1m, 2m]);

        var executors = request.Build(Seeded);

        Assert.Null(request.Validate());
        Assert.Equal([1L, 2L, 3L], executors.Select(e => e.Id));
        Assert.Equal("Иванов И. 1", executors[0].FullName);
        Assert.All(executors, e => Assert.True(e.Attributes.ContainsKey("subjects")));
        Assert.NotNull(new SeedExecutorsRequest(0, []).Validate());
        Assert.NotNull(new SeedExecutorsRequest(5, [], Qualifications: [500m]).Validate());
    }
}
