using ExecutorBalancer.Application.Analytics;

namespace ExecutorBalancer.Tests;

public class FairShareTests
{
    private static Dictionary<long, decimal> Allocate(Dictionary<long, decimal> qualification, params FairPool[] pools) =>
        FairShare.Allocate(pools, qualification);

    [Fact]
    public void CommonPoolIsSplitByQualification()
    {
        var result = Allocate(new() { [1] = 1m, [2] = 3m }, new FairPool([1, 2], 40m));

        Assert.Equal(10m, result[1], 3);
        Assert.Equal(30m, result[2], 3);
    }

    [Fact]
    public void OrdersOnlyOneExecutorCanTakeShiftSharedOrdersToTheOther()
    {
        // первый берёт кредиты и карты, второй только кредиты: пропорциональный делёж дал бы 15 и 5,
        // а равная нагрузка — 10 и 10
        var result = Allocate(new() { [1] = 1m, [2] = 1m },
            new FairPool([1, 2], 10m),
            new FairPool([1], 10m));

        Assert.Equal(10m, result[1], 3);
        Assert.Equal(10m, result[2], 3);
    }

    [Fact]
    public void OverloadedSpecialistCannotBeEqualised()
    {
        var result = Allocate(new() { [1] = 1m, [2] = 1m },
            new FairPool([2], 20m),
            new FairPool([1, 2], 4m));

        Assert.Equal(4m, result[1], 3);
        Assert.Equal(20m, result[2], 3);
    }

    [Fact]
    public void ChainOfOverlapsIsLevelledAcrossAllExecutors()
    {
        var result = Allocate(new() { [1] = 1m, [2] = 2m, [3] = 1m },
            new FairPool([1], 2m),
            new FairPool([1, 2], 10m),
            new FairPool([2, 3], 10m),
            new FairPool([3], 2m));

        // всего 24 на квалификацию 4 — по 6 на единицу
        Assert.Equal(6m, result[1], 3);
        Assert.Equal(12m, result[2], 3);
        Assert.Equal(6m, result[3], 3);
        Assert.Equal(24m, result.Values.Sum(), 3);
    }

    [Fact]
    public void UnknownExecutorsAndEmptyPoolsAreIgnored()
    {
        var result = Allocate(new() { [1] = 1m },
            new FairPool([1, 99], 5m),
            new FairPool([99], 7m),
            new FairPool([1], 0m));

        Assert.Equal(5m, Assert.Single(result).Value, 3);
    }
}
