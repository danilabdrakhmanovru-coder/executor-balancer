using ExecutorBalancer.Application.Balancing;

namespace ExecutorBalancer.Tests;

/// <summary>
/// Алгоритм выбора под параллельной нагрузкой. Сквозной тест на Redis и PostgreSQL
/// с несколькими экземплярами сервиса — на этапе 2.
/// </summary>
public class ConcurrencyTests
{
    [Fact]
    public async Task ParallelOrdersAreNeverBookedTwice()
    {
        var store = new InMemoryLoadStore();
        var executors = Enumerable.Range(1, 10).Select(i => (long)i).ToArray();
        foreach (var id in executors)
        {
            await store.SetActiveAsync(id, true, CancellationToken.None);
        }

        var slots = executors.Select(id => new CandidateSlot(id, id % 3 == 0 ? 2000 : 1000, null)).ToList();
        var day = new DateOnly(2026, 9, 28);
        var orders = Enumerable.Range(1, 2000).ToArray();

        // каждую заявку отправляем дважды одновременно — как при повторной доставке от АИС
        var results = await Task.WhenAll(orders.Concat(orders).Select(id => Task.Run(() =>
            store.PickAsync(new PickRequest(id, 1000, day, false, slots), CancellationToken.None))));

        Assert.Equal(2000, results.Count(r => r.Status == PickStatus.Assigned));
        Assert.Equal(2000, results.Count(r => r.Status == PickStatus.AlreadyAssigned));
        Assert.Equal(2000, executors.Sum(store.OpenCount));
        Assert.Equal(2000 * 1000L, executors.Sum(store.OpenWeight));

        // нагрузка на единицу квалификации различается не больше чем на одну заявку
        var perQualification = slots.Select(s => (decimal)store.OpenWeight(s.ExecutorId) / s.QualificationMilli).ToList();
        Assert.True(perQualification.Max() - perQualification.Min() <= 1m);
    }

    [Fact]
    public async Task DailyLimitHoldsUnderContention()
    {
        var store = new InMemoryLoadStore();
        await store.SetActiveAsync(1, true, CancellationToken.None);
        var slots = new List<CandidateSlot> { new(1, 1000, 50) };
        var day = new DateOnly(2026, 9, 28);

        var results = await Task.WhenAll(Enumerable.Range(1, 500).Select(id => Task.Run(() =>
            store.PickAsync(new PickRequest(id, 1000, day, false, slots), CancellationToken.None))));

        Assert.Equal(50, results.Count(r => r.Status == PickStatus.Assigned));
        Assert.Equal(450, results.Count(r => r.Status == PickStatus.NoCandidate));
    }

    [Theory]
    [InlineData(1000L, 1000L, 0L, 1L, 1000L, 1000L, 0L, 2L, true)]
    [InlineData(1000L, 1000L, 5L, 1L, 1000L, 1000L, 1L, 2L, false)]
    [InlineData(3000L, 2000L, 0L, 9L, 2000L, 1000L, 0L, 1L, true)]
    public void TieBreakingIsDeterministic(long loadA, long qualA, long dailyA, long idA,
        long loadB, long qualB, long dailyB, long idB, bool expected)
    {
        // за час получили поровну — решают открытая нагрузка, затем назначения за сутки, затем номер
        Assert.Equal(expected, LoadMath.IsBetter(1000, 0, loadA, qualA, dailyA, idA, 0, loadB, qualB, dailyB, idB));
    }

    [Theory]
    [InlineData(2000L, 0L, 1000L, 3000L, false)] // A быстро закрывает (в работе пусто), но за час получил больше — не он
    [InlineData(1000L, 5000L, 2000L, 0L, true)]  // A загружен сильнее, но за час получил меньше — он
    public void HourWeightComesFirst(long hourA, long loadA, long hourB, long loadB, bool expected) =>
        Assert.Equal(expected, LoadMath.IsBetter(1000, hourA, loadA, 1000, 0, 1, hourB, loadB, 1000, 0, 2));
}
