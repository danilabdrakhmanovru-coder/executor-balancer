using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Tests;

/// <summary>Аналитика за последние 5 и 15 минут: по минутам, прямо из назначений, с тем же эталоном справедливости.</summary>
public class RecentAnalyticsTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Theory]
    [InlineData(AnalyticsPeriod.Last5Minutes, 5)]
    [InlineData(AnalyticsPeriod.Last15Minutes, 15)]
    public async Task TwoEqualExecutorsSplitEvenly(AnalyticsPeriod period, int minutes)
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        for (var id = 1; id <= 40; id++)
        {
            await _f.Receive(id);
        }

        await _f.ChangeStatus(1, OrderStatus.Accept);
        await _f.ChangeStatus(2, OrderStatus.Await);

        var report = await _f.Analytics(period);

        Assert.Equal(1, report.BucketMinutes);
        Assert.Equal(minutes, report.Timeline.Count);
        Assert.Equal(40, report.Timeline.Sum(p => p.Assigned));
        Assert.Equal(1, report.Timeline.Sum(p => p.Closed));
        Assert.Equal(1, report.Timeline.Sum(p => p.Returned));
        Assert.All(report.Executors, e => Assert.Equal(20, e.Assigned - e.Secondary));
        Assert.All(report.Executors, e => Assert.Null(e.Rank));
        Assert.True(report.Fairness.MaxAbsDeviationPercent is not null and <= 10m,
            $"перекос {report.Fairness.MaxAbsDeviationPercent}%");
    }

    [Fact]
    public async Task HourlyPeriodsStayHourly()
    {
        await _f.AddExecutor(1);
        await _f.Receive(1);

        var report = await _f.Analytics(AnalyticsPeriod.Today);

        Assert.Equal(0, report.BucketMinutes);
        Assert.Equal(1, report.BucketHours);
    }
}
