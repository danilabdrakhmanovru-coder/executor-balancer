using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>
/// История квалификации: справедливая доля за прошлые часы считается по тогдашней квалификации, а не по сегодняшней —
/// иначе смена квалификации днём (или пересоздание тестовых сотрудников) выдавала бы ложный «перекос».
/// </summary>
public class QualificationHistoryTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task ChangesAreRecorded()
    {
        await _f.AddExecutor(1, qualification: 1m);
        await _f.AddExecutor(1, qualification: 1m); // без изменений — новой записи нет
        await _f.AddExecutor(1, qualification: 2.5m);

        var history = await _f.Query(db => db.ExecutorQualifications.AsNoTracking().Where(q => q.ExecutorId == 1)
            .OrderBy(q => q.Id).Select(q => q.Qualification).ToListAsync());
        Assert.Equal([1m, 2.5m], history);
    }

    [Fact]
    public async Task ExecutorWithoutHistoryKeepsOldValueForThePast()
    {
        // сотрудник заведён до появления истории (как в базе, обновлённой со старой версии)
        await _f.Query(async db =>
        {
            db.Executors.Add(new Executor { Id = 7, FullName = "Старожил", IsActive = true, QualificationWeight = 1m });
            return await db.SaveChangesAsync();
        });

        await _f.AddExecutor(7, qualification: 3m);

        var history = await _f.Query(db => db.ExecutorQualifications.AsNoTracking().Where(q => q.ExecutorId == 7)
            .OrderBy(q => q.Id).ToListAsync());
        Assert.Equal([1m, 3m], history.Select(q => q.Qualification));
        Assert.Equal(DateTimeOffset.UnixEpoch, history[0].ValidFrom); // прежнее значение — «с самого начала»
    }

    [Fact]
    public async Task FairShareUsesQualificationOfThatHour()
    {
        await _f.AddExecutor(1, qualification: 1m);
        await _f.AddExecutor(2, qualification: 1m);
        await _f.AddExecutor(1, qualification: 3m); // квалификацию подняли только сейчас

        // два часа назад оба были равны и получили поровну
        var hour = ExecutorStats.HourOf(DateTimeOffset.UtcNow) - 2;
        await _f.Query(async db =>
        {
            db.ExecutorHourStats.AddRange(
                new ExecutorHourStat { BucketHour = hour, ExecutorId = 1, DepartmentId = D, AssignedCount = 10, AssignedWeight = 10, PrimaryCount = 10, FreeWeight = 10 },
                new ExecutorHourStat { BucketHour = hour, ExecutorId = 2, DepartmentId = D, AssignedCount = 10, AssignedWeight = 10, PrimaryCount = 10, FreeWeight = 10 });
            db.EligibilityHourStats.Add(new EligibilityHourStat { BucketHour = hour, DepartmentId = D, SetKey = "1,2", Count = 20, Weight = 20 });
            return await db.SaveChangesAsync();
        });

        var report = await _f.Analytics(AnalyticsPeriod.Day);

        var first = report.Executors.Single(e => e.Id == 1);
        Assert.Equal(10m, first.FairWeight); // по тогдашней квалификации 1, а не сегодняшней 3 (было бы 15)
        Assert.Equal(0m, first.DeviationPercent);
        Assert.Equal(0m, report.Fairness.MeanAbsDeviationPercent);
    }
}
