using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Domain;

namespace ExecutorBalancer.Tests;

/// <summary>
/// Суточный лимит закончился посреди часа. Эталон идёт по пятиминуткам по порядку и не требует от выбора
/// предвидения: пока все подходили, нагрузка делилась поровну, а после — доставалась оставшимся.
/// </summary>
public class MidHourLimitTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    /// <summary>
    /// Первые пять минут: заявки «1 или 2» и «1 или 3», все делят поровну, первый набирает лимит (14 заявок).
    /// Следующие пять минут: заявки, которые может взять только третий. Считай эталон за час целиком, он решил бы
    /// задним числом, что первому стоило брать только заявки «1 или 3», и требовал бы от второго 20 вместо 13.
    /// </summary>
    [Fact]
    public async Task ReferenceFollowsTheOrderOfSlots()
    {
        await _f.AddExecutor(1, dailyLimit: 14);
        await _f.AddExecutor(2);
        await _f.AddExecutor(3);

        var hour = ExecutorStats.HourOf(DateTimeOffset.UtcNow) - 2;
        await _f.Query(async db =>
        {
            db.ExecutorHourStats.AddRange(Row(hour, 1, 14), Row(hour, 2, 13), Row(hour, 3, 23));
            db.EligibilityHourStats.AddRange(
                Pool(hour, 0, "1,2", 20), Pool(hour, 0, "1,3", 20),
                Pool(hour, 1, "3", 10));
            return await db.SaveChangesAsync();
        });

        var report = await _f.Analytics(AnalyticsPeriod.Day);

        decimal Fair(long id) => Math.Round(report.Executors.Single(e => e.Id == id).FairWeight, 2);
        Assert.Equal((13.33m, 13.33m, 23.33m), (Fair(1), Fair(2), Fair(3)));
        Assert.True(report.Fairness.MaxAbsDeviationPercent < 6m, $"перекос {report.Fairness.MaxAbsDeviationPercent}%");
    }

    /// <summary>Назначения без выбора (от родителя) делятся между пятиминутками пропорционально потоку и остаются в эталоне.</summary>
    [Fact]
    public async Task ForcedWeightStaysInReference()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);

        var hour = ExecutorStats.HourOf(DateTimeOffset.UtcNow) - 2;
        await _f.Query(async db =>
        {
            // первый получил 4 заявки от родителя, остальное — свободный выбор
            db.ExecutorHourStats.AddRange(Row(hour, 1, 12, forced: 4), Row(hour, 2, 12));
            db.EligibilityHourStats.AddRange(Pool(hour, 0, "1,2", 10), Pool(hour, 3, "1,2", 10));
            return await db.SaveChangesAsync();
        });

        var report = await _f.Analytics(AnalyticsPeriod.Day);

        Assert.All(report.Executors, e => Assert.Equal(12m, Math.Round(e.FairWeight, 3)));
        Assert.Equal(0m, report.Fairness.MeanAbsDeviationPercent);
    }

    private static ExecutorHourStat Row(long hour, long executor, int weight, int forced = 0) => new()
    {
        BucketHour = hour, ExecutorId = executor, DepartmentId = D, AssignedCount = weight, AssignedWeight = weight,
        PrimaryCount = weight - forced, ParentCount = forced, FreeWeight = weight - forced,
    };

    private static EligibilityHourStat Pool(long hour, int slot, string set, decimal weight) => new()
    {
        BucketHour = hour, Slot = slot, DepartmentId = D, SetKey = set, Count = (int)weight, Weight = weight,
    };
}
