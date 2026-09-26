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
        Assert.Equal(expected, LoadMath.IsBetter(1000, loadA, qualA, dailyA, idA, loadB, qualB, dailyB, idB));
    }
}
