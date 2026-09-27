using ExecutorBalancer.Application;
using ExecutorBalancer.Application.Analytics;
using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Application.Configuration;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

/// <summary>Рейтинг (балл = вес × качество), режим «больше нормы» и защита от работы на количество.</summary>
public class MotivationTests : IAsyncLifetime
{
    private static readonly MotivationInput Defaults = new(20, 0.25m, 0.5m, 30, 0.8m, 0.9m, 3m);

    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    private Task<MotivationInput?> SetMotivation(MotivationInput input) => _f.Config(c => c.UpdateMotivationAsync(D, input, CancellationToken.None));

    private Task<bool> SetExtra(long id, int percent) =>
        _f.Config(c => c.SetExtraModeAsync(D, id, new ExtraModeInput(percent), CancellationToken.None));

    private Task<Order> Order(long id) => _f.Query(db => db.Orders.AsNoTracking().FirstAsync(o => o.Id == id));

    [Fact]
    public async Task PointsAreWeightTimesQuality()
    {
        await SetMotivation(Defaults with { FastCloseSeconds = 0 });
        await _f.AddExecutor(1);
        await _f.Receive(1, attributes: BalancerFixture.DefaultOrder(clientClass: "VIP")); // вес 3
        await _f.Receive(2);                                                               // вес 1

        await _f.ChangeStatus(1, OrderStatus.Accept);
        await _f.ChangeStatus(2, OrderStatus.Await);      // доработка
        await _f.ChangeStatus(2, OrderStatus.Processed);  // вернулась к нему же
        await _f.ChangeStatus(2, OrderStatus.Accept);

        Assert.Equal(3m, (await Order(1)).Points);
        Assert.Equal(0.75m, (await Order(2)).Points);     // 1 × (1 − 0,25)
        var report = await _f.Analytics();
        var me = Assert.Single(report.Executors);
        Assert.Equal(3.75m, me.Points);
        Assert.Equal(4m, me.ClosedWeight);
    }

    [Fact]
    public async Task StatusChangesAreRecordedForOrderPath()
    {
        await _f.AddExecutor(1);
        await _f.Receive(1);
        await _f.ChangeStatus(1, OrderStatus.Await);
        await _f.ChangeStatus(1, OrderStatus.Processed);
        await _f.ChangeStatus(1, OrderStatus.Accept);
        await _f.ChangeStatus(1, OrderStatus.Accept); // повтор из АИС — без новой записи

        var path = await _f.Query(db => db.OrderStatusChanges.AsNoTracking().Where(c => c.OrderId == 1).OrderBy(c => c.Id)
            .Select(c => c.To).ToListAsync());
        Assert.Equal([OrderStatus.Await, OrderStatus.Processed, OrderStatus.Accept], path);
    }

    [Fact]
    public async Task FastCloseLowersQuality()
    {
        await SetMotivation(Defaults with { FastCloseSeconds = 3600 }); // любое закрытие в тесте — «слишком быстрое»
        await _f.AddExecutor(1);
        await _f.Receive(1);
        await _f.ChangeStatus(1, OrderStatus.Reject);

        Assert.Equal(0.5m, (await Order(1)).Points);
        Assert.Equal(1, (await _f.Analytics()).Executors.Single().FastClosed);
    }

    [Fact]
    public async Task VolunteerGetsOnlySurplus()
    {
        await _f.AddExecutor(1, dailyLimit: 1);
        await _f.AddExecutor(2, dailyLimit: 1);
        await SetExtra(1, 30); // норма 1 → потолок 2

        Assert.Equal(1, (await _f.Receive(1)).ExecutorId);
        // у 1 норма набрана, у 2 — нет: доброволец не забирает заявку у коллеги
        Assert.Equal(2, (await _f.Receive(2)).ExecutorId);
        var surplus = await _f.Receive(3);
        Assert.Equal(1, surplus.ExecutorId);
        Assert.Equal(AssignmentKind.Extra, surplus.Kind);
        Assert.Equal(BalanceOutcome.Pending, (await _f.Receive(4)).Outcome); // потолок режима набран

        var assignment = await _f.Query(db => db.Assignments.AsNoTracking().FirstAsync(a => a.OrderId == 3));
        Assert.Contains("сверх нормы", AssignmentExplanation.FromJson(assignment.ExplanationJson)!.Decision);
        Assert.Equal(1, (await _f.Analytics()).Kinds.Extra);
    }

    [Fact]
    public async Task ExtraModeIsSuspendedWhenQualityDrops()
    {
        await SetMotivation(Defaults with { FastCloseSeconds = 3600 }); // всё закрывается «слишком быстро»: качество 50%
        await _f.AddExecutor(1, dailyLimit: 6);
        await SetExtra(1, 30);
        for (var i = 1; i <= 6; i++)
        {
            await _f.Receive(i);
            await _f.ChangeStatus(i, OrderStatus.Accept);
        }

        _f.Shared<QualityTracker>().Invalidate();
        var result = await _f.Receive(7);

        Assert.Equal(BalanceOutcome.Pending, result.Outcome);
        var preview = await _f.Run(b => b.PreviewAsync(D, null, BalancerFixture.Attributes(BalancerFixture.DefaultOrder()),
            CancellationToken.None));
        Assert.Contains("приостановлен", preview.Candidates.Single().Reason);
    }

    [Fact]
    public async Task HeavyOrdersOverNormNeedConfirmedQuality()
    {
        await _f.AddExecutor(1, dailyLimit: 1);
        await SetExtra(1, 30);
        await _f.Receive(1);

        // качество ещё не оценено: обычная заявка сверх нормы — можно, сложная (VIP, вес 3) — нет
        Assert.Equal(BalanceOutcome.Pending,
            (await _f.Receive(2, attributes: BalancerFixture.DefaultOrder(clientClass: "VIP"))).Outcome);
        Assert.Equal(1, (await _f.Receive(3)).ExecutorId);
    }

    [Fact]
    public async Task ExtraModeRespectsDepartmentCeilingAndNorm()
    {
        await _f.AddExecutor(1, dailyLimit: 10);
        await _f.AddExecutor(2, dailyLimit: null);

        await Assert.ThrowsAsync<InvalidInputException>(() => SetExtra(1, 50));         // потолок отдела 30%
        await Assert.ThrowsAsync<ConfigurationConflictException>(() => SetExtra(2, 10)); // без нормы режим не нужен
        var support = await _f.Departments(async d => (await d.FindByCodeAsync("support", CancellationToken.None))!.Value);
        Assert.False(await _f.Config(c => c.SetExtraModeAsync(support, 1, new ExtraModeInput(10), CancellationToken.None)));

        await SetExtra(1, 20);
        var audit = await _f.Config(c => c.GetAuditAsync(D, null, 5, CancellationToken.None));
        Assert.Contains(audit, a => a.Action == "extra_mode_changed");
    }

    [Fact]
    public async Task ReturnFromReworkIsNotANewOrderForTheDay()
    {
        await _f.AddExecutor(1, dailyLimit: 2);
        await _f.Receive(1);
        await _f.ChangeStatus(1, OrderStatus.Await);
        var back = await _f.ChangeStatus(1, OrderStatus.Processed); // вернулась к нему же — вторичная

        Assert.Equal(AssignmentKind.Secondary, back!.Kind);
        var today = await _f.Run(b => Task.FromResult(b.Today()));
        Assert.Equal(1, _f.Store.AssignedOn(today, 1));
        Assert.Equal(1, (await _f.Receive(2)).ExecutorId); // место в норме осталось для новой заявки
    }

    [Fact]
    public async Task LowQualityVolumeDoesNotWinRating()
    {
        await SetMotivation(Defaults with { FastCloseSeconds = 0 });
        await _f.AddExecutor(1, subjects: ["кредит"]);
        await _f.AddExecutor(2, subjects: ["вклад"]);
        // сотрудник 1 закрывает много, но каждую заявку — с тремя доработками (качество 25%)
        for (var i = 1; i <= 8; i++)
        {
            await _f.Receive(i);
            for (var r = 0; r < 3; r++)
            {
                await _f.ChangeStatus(i, OrderStatus.Await);
                await _f.ChangeStatus(i, OrderStatus.Processed);
            }

            await _f.ChangeStatus(i, OrderStatus.Accept);
        }

        // сотрудник 2 закрыл меньше, но чисто
        for (var i = 101; i <= 105; i++)
        {
            await _f.Receive(i, attributes: BalancerFixture.DefaultOrder(subject: "вклад"));
            await _f.ChangeStatus(i, OrderStatus.Accept);
        }

        var report = await _f.Analytics();
        var hasty = report.Executors.Single(e => e.Id == 1);
        var careful = report.Executors.Single(e => e.Id == 2);
        Assert.Equal(0.25m, hasty.Quality);
        Assert.Null(hasty.Rank);           // вне рейтинга, хотя закрыл больше
        Assert.Equal(1, careful.Rank);
    }

    [Theory]
    [InlineData(-1, 0.25, 0.5, 30, 0.8, 0.9, 3)]
    [InlineData(20, 1.5, 0.5, 30, 0.8, 0.9, 3)]
    [InlineData(20, 0.25, 0.5, 101, 0.8, 0.9, 3)]
    [InlineData(20, 0.25, 0.5, 30, 0.9, 0.8, 3)]
    [InlineData(20, 0.25, 0.5, 30, 0.8, 0.9, 0)]
    public async Task InvalidMotivationIsRejected(int fast, double rework, double fastPenalty, int extra, double threshold,
        double heavyThreshold, double heavyWeight) =>
        await Assert.ThrowsAsync<InvalidInputException>(() => SetMotivation(new MotivationInput(fast, (decimal)rework,
            (decimal)fastPenalty, extra, (decimal)threshold, (decimal)heavyThreshold, (decimal)heavyWeight)));

    [Fact]
    public void QualityNeverDropsBelowMinimum()
    {
        var m = Motivation.From(new Department());
        Assert.Equal(Motivation.MinQuality, m.QualityOf(10, TimeSpan.FromSeconds(1), out var fast));
        Assert.True(fast);
        Assert.Equal(1m, m.QualityOf(0, TimeSpan.FromMinutes(5), out _));
        Assert.Null(Motivation.QualityFrom(Motivation.MinClosedForQuality - 1, 10, 10));
    }
}
