using ExecutorBalancer.Application.Balancing;
using ExecutorBalancer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExecutorBalancer.Tests;

public class OrderBalancerTests : IAsyncLifetime
{
    private BalancerFixture _f = null!;

    public async Task InitializeAsync() => _f = await BalancerFixture.CreateAsync();

    public async Task DisposeAsync() => await _f.DisposeAsync();

    [Fact]
    public async Task FastCloserDoesNotGetMoreThanFairShare()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        var got = new Dictionary<long, int> { [1] = 0, [2] = 0 };
        for (var i = 1; i <= 20; i++)
        {
            var result = await _f.Receive(i);
            got[result.ExecutorId!.Value]++;
            if (result.ExecutorId == 1)
            {
                await _f.ChangeStatus(i, OrderStatus.Accept); // первый закрывает сразу, у второго копится
            }
        }

        // по открытой нагрузке первый получал бы почти всё; по полученному за час — поровну
        Assert.Equal(10, got[1]);
        Assert.Equal(10, got[2]);
    }

    [Fact]
    public async Task InactiveExecutorIsNotAssigned()
    {
        await _f.AddExecutor(1, active: false);
        await _f.AddExecutor(2);

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(2, (await _f.Receive(i)).ExecutorId);
        }
    }

    [Fact]
    public async Task ExecutorMustMatchAllParameters()
    {
        await _f.AddExecutor(1, subjects: ["вклад"]);
        await _f.AddExecutor(2, subjects: ["кредит"]);

        var result = await _f.Receive(10, attributes: BalancerFixture.DefaultOrder(subject: "кредит"));

        Assert.Equal(2, result.ExecutorId);
        var explanation = await Explanation(10);
        var rejected = Assert.Single(explanation.Candidates, c => c.ExecutorId == 1);
        Assert.Equal("rule_failed", rejected.Verdict);
        Assert.Contains("Тематика", rejected.Reason);
    }

    [Fact]
    public async Task DailyLimitIsRespected()
    {
        await _f.AddExecutor(1, dailyLimit: 2);

        await _f.Receive(1);
        await _f.Receive(2);
        var third = await _f.Receive(3);

        Assert.Equal(BalanceOutcome.Pending, third.Outcome);
        Assert.Contains("лимит", third.Reason);
    }

    [Fact]
    public async Task ExecutorWithoutLimitKeepsReceivingOrders()
    {
        await _f.AddExecutor(1, dailyLimit: null);

        for (var i = 1; i <= 50; i++)
        {
            Assert.Equal(1, (await _f.Receive(i)).ExecutorId);
        }
    }

    [Fact]
    public async Task ChildOrderGoesToParentExecutorEvenOverLimit()
    {
        await _f.AddExecutor(1, dailyLimit: 1);
        await _f.AddExecutor(2);
        var owner = (await _f.Receive(1)).ExecutorId;

        for (var i = 2; i <= 4; i++)
        {
            var child = await _f.Receive(i, parentId: 1);
            Assert.Equal(owner, child.ExecutorId);
            Assert.Equal(AssignmentKind.Parent, child.Kind);
        }
    }

    /// <summary>
    /// Дочерняя заявка вернулась с доработки: это возврат к тому же сотруднику, а не новое продолжение —
    /// в суточную норму повторно не засчитывается.
    /// </summary>
    [Fact]
    public async Task ChildOrderBackFromReworkIsSecondaryAndNotCountedAgain()
    {
        await _f.AddExecutor(1, dailyLimit: 3);
        await _f.Receive(1);
        Assert.Equal(AssignmentKind.Parent, (await _f.Receive(2, parentId: 1)).Kind);

        await _f.ChangeStatus(2, OrderStatus.Await);
        var back = await _f.ChangeStatus(2, OrderStatus.Processed);
        Assert.Equal(1, back!.ExecutorId);
        Assert.Equal(AssignmentKind.Secondary, back.Kind);

        // за день у него 2 из 3: следующая обычная заявка ещё помещается в норму
        Assert.Equal(1, (await _f.Receive(3)).ExecutorId);
        Assert.Null((await _f.Receive(4)).ExecutorId);
    }

    [Fact]
    public async Task ParentExecutorThatNoLongerMatchesIsSkipped()
    {
        await _f.AddExecutor(1);
        await _f.Receive(1);
        await _f.AddExecutor(2);
        await _f.AddExecutor(1, subjects: ["вклад"]);

        var child = await _f.Receive(2, parentId: 1);

        Assert.Equal(2, child.ExecutorId);
        Assert.Equal(AssignmentKind.Primary, child.Kind);
    }

    [Fact]
    public async Task SecondaryOrderReturnsToPreviousExecutor()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        var owner = (await _f.Receive(1)).ExecutorId!.Value;
        await _f.Receive(2);
        await _f.Receive(3);

        await _f.ChangeStatus(1, OrderStatus.Await);
        Assert.Equal(await OpenOrdersOf(owner), _f.Store.OpenCount(owner));
        var back = await _f.ChangeStatus(1, OrderStatus.Processed);

        Assert.Equal(owner, back!.ExecutorId);
        Assert.Equal(AssignmentKind.Secondary, back.Kind);
    }

    [Fact]
    public async Task SecondaryOrderIsReassignedWhenExecutorBecameInactive()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        await _f.Receive(1);
        await _f.ChangeStatus(1, OrderStatus.Await);
        await _f.AddExecutor(1, active: false);

        var back = await _f.ChangeStatus(1, OrderStatus.Processed);

        Assert.Equal(2, back!.ExecutorId);
        Assert.Equal(AssignmentKind.Reassign, back.Kind);
        var current = await _f.Query(db => db.Assignments.CountAsync(a => a.OrderId == 1 && a.IsCurrent));
        Assert.Equal(1, current);
    }

    [Fact]
    public async Task QualificationWeightShiftsLoadToStrongerExecutor()
    {
        await _f.AddExecutor(1, qualification: 1m);
        await _f.AddExecutor(2, qualification: 2m);

        var picks = new List<long?>();
        for (var i = 1; i <= 3; i++)
        {
            picks.Add((await _f.Receive(i)).ExecutorId);
        }

        // 1: (0+1)/1 = 1 против (0+1)/2 = 0.5 → второй
        // 2: 1 против (1+1)/2 = 1 → равенство, у первого меньше назначений → первый
        // 3: (1+1)/1 = 2 против 1 → второй
        Assert.Equal(new long?[] { 2, 1, 2 }, picks);
    }

    [Fact]
    public async Task HeavyOrderCountsMoreThanLightOne()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);

        var heavy = await _f.Receive(1, attributes: BalancerFixture.DefaultOrder(sum: 2_000_000));
        await _f.Receive(2);
        await _f.Receive(3);

        Assert.Equal(1, heavy.ExecutorId);
        Assert.Equal(1, _f.Store.OpenCount(1));
        Assert.Equal(2, _f.Store.OpenCount(2));
    }

    [Fact]
    public async Task EqualScoreIsResolvedByLowestId()
    {
        await _f.AddExecutor(7);
        await _f.AddExecutor(3);

        Assert.Equal(3, (await _f.Receive(1)).ExecutorId);
    }

    [Fact]
    public async Task RepeatedOrderIdIsNotAssignedAgain()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);

        var first = await _f.Receive(5);
        var second = await _f.Receive(5);

        Assert.True(second.Duplicate);
        Assert.Equal(first.ExecutorId, second.ExecutorId);
        Assert.Equal(1, _f.Store.OpenCount(first.ExecutorId!.Value));
        Assert.Equal(1, await _f.Query(db => db.Assignments.CountAsync(a => a.OrderId == 5)));
    }

    [Fact]
    public async Task OrderWithoutSuitableExecutorStaysPending()
    {
        await _f.AddExecutor(1, subjects: ["вклад"]);

        var result = await _f.Receive(1);

        Assert.Equal(BalanceOutcome.Pending, result.Outcome);
        Assert.Null(result.ExecutorId);
        Assert.Contains("подходящего", result.Reason);
    }

    [Fact]
    public async Task ClosingOrderReleasesLoad()
    {
        await _f.AddExecutor(1);
        await _f.Receive(1);

        await _f.ChangeStatus(1, OrderStatus.Accept);

        Assert.Equal(0, _f.Store.OpenCount(1));
        Assert.Equal(0, _f.Store.OpenWeight(1));
    }

    [Fact]
    public async Task ExplanationListsScoresOfEligibleCandidates()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2, qualification: 2m);
        await _f.AddExecutor(3, active: false);

        await _f.Receive(1);
        var explanation = await Explanation(1);

        Assert.Equal(2, explanation.ChosenExecutorId);
        Assert.Equal(0.5m, explanation.ChosenScore);
        Assert.Equal("inactive", explanation.Candidates.Single(c => c.ExecutorId == 3).Verdict);
        Assert.Equal(1m, explanation.Candidates.Single(c => c.ExecutorId == 1).Score);
    }

    [Fact]
    public async Task DeactivatedExecutorLosesOpenOrders()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        for (var i = 1; i <= 4; i++)
        {
            await _f.Receive(i);
        }

        await _f.AddExecutor(1, active: false);

        var owners = await _f.Query(db => db.Orders.Select(o => o.ExecutorId).ToListAsync());
        Assert.All(owners, owner => Assert.Equal(2, owner));
        Assert.Equal(0, _f.Store.OpenCount(1));
        Assert.Equal(4, _f.Store.OpenCount(2));
    }

    [Fact]
    public async Task PendingOrderIsAssignedWhenExecutorAppears()
    {
        await _f.AddExecutor(1, subjects: ["вклад"]);
        var pending = await _f.Receive(1);
        Assert.Equal(BalanceOutcome.Pending, pending.Outcome);

        await _f.AddExecutor(2);
        var retried = await _f.Run(b => b.RetryPendingAsync(1, CancellationToken.None));

        Assert.Equal(2, retried!.ExecutorId);
    }

    [Fact]
    public async Task OrderReturnedFromAwaitWithoutCandidatesCanBeRetried()
    {
        await _f.AddExecutor(1);
        await _f.Receive(1);
        await _f.ChangeStatus(1, OrderStatus.Await);
        await _f.AddExecutor(1, active: false);

        var back = await _f.ChangeStatus(1, OrderStatus.Processed);
        Assert.Equal(BalanceOutcome.Pending, back!.Outcome);

        await _f.AddExecutor(2);
        var retried = await _f.Run(b => b.RetryPendingAsync(1, CancellationToken.None));
        Assert.Equal(2, retried!.ExecutorId);
    }

    private Task<int> OpenOrdersOf(long executorId) =>
        _f.Query(db => db.Orders.CountAsync(o => o.ExecutorId == executorId && o.Status == OrderStatus.Processed));

    private async Task<AssignmentExplanation> Explanation(long orderId)
    {
        var json = await _f.Query(db => db.Assignments
            .Where(a => a.OrderId == orderId && a.IsCurrent)
            .Select(a => a.ExplanationJson)
            .SingleAsync());
        return AssignmentExplanation.FromJson(json)!;
    }

    [Fact]
    public async Task OrphanedRedisHoldIsRolledBackAndOrderAssignedAgain()
    {
        await _f.Receive(1);
        await _f.AddExecutor(1);
        // процесс «упал» между выбором в Redis и записью в базу
        var day = await _f.Run(b => Task.FromResult(b.Today()));
        await _f.Store.PickAsync(new PickRequest(1, 1000, day, false, [new CandidateSlot(1, 1000, null)]),
            CancellationToken.None);
        _f.Store.Backdate(1, TimeSpan.FromMinutes(5));

        var retried = await _f.Run(b => b.RetryPendingAsync(1, CancellationToken.None));

        Assert.Equal(1, retried!.ExecutorId);
        Assert.False(retried.Duplicate);
        Assert.Equal(1, _f.Store.OpenCount(1));
        Assert.Equal(1, _f.Store.AssignedOn(day, 1));
        Assert.Equal(1, await _f.Query(db => db.Orders.Where(o => o.Id == 1).Select(o => o.ExecutorId).FirstAsync()));
    }

    [Fact]
    public async Task FreshHoldOfAnotherInstanceIsNotTouched()
    {
        await _f.Receive(1);
        await _f.AddExecutor(1);
        var day = await _f.Run(b => Task.FromResult(b.Today()));
        await _f.Store.PickAsync(new PickRequest(1, 1000, day, false, [new CandidateSlot(1, 1000, null)]),
            CancellationToken.None);

        var retried = await _f.Run(b => b.RetryPendingAsync(1, CancellationToken.None));

        Assert.True(retried!.Duplicate);
        Assert.Null(await _f.Query(db => db.Orders.Where(o => o.Id == 1).Select(o => o.ExecutorId).FirstAsync()));
    }

    [Fact]
    public async Task RollbackReturnsDailySlot()
    {
        await _f.AddExecutor(1, dailyLimit: 1);
        var day = await _f.Run(b => Task.FromResult(b.Today()));
        await _f.Store.PickAsync(new PickRequest(1, 1000, day, false, [new CandidateSlot(1, 1000, 1)]), CancellationToken.None);
        await _f.Store.ReleaseAsync(1, LoadRelease.Rollback, CancellationToken.None);

        Assert.Equal(0, _f.Store.AssignedOn(day, 1));
        Assert.Equal(1, (await _f.Receive(2)).ExecutorId);
    }

    [Fact]
    public async Task HourlyStatsFollowAssignmentsAndStatuses()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2);
        for (var i = 1; i <= 4; i++)
        {
            await _f.Receive(i);
        }

        await _f.Receive(5, parentId: 1);
        await _f.ChangeStatus(2, OrderStatus.Accept);
        await _f.ChangeStatus(3, OrderStatus.Await);

        var stats = await _f.Query(db => db.ExecutorHourStats.ToListAsync());
        Assert.Equal(5, stats.Sum(s => s.AssignedCount));
        Assert.Equal(4, stats.Sum(s => s.PrimaryCount));
        Assert.Equal(1, stats.Sum(s => s.ParentCount));
        Assert.Equal(1, stats.Sum(s => s.ClosedCount));
        Assert.Equal(1, stats.Sum(s => s.ReturnedCount));
        var pools = await _f.Query(db => db.EligibilityHourStats.ToListAsync());
        var pool = Assert.Single(pools, p => !p.SetKey.StartsWith('='));
        Assert.Equal("1,2", pool.SetKey);
        Assert.Equal(4, pool.Count);
        // заявка от родителя — без выбора: отдельная строка «=исполнитель» в той же пятиминутке
        var parentExecutor = await _f.Query(db => db.Orders.Where(o => o.Id == 1).Select(o => o.ExecutorId).SingleAsync());
        var pinned = Assert.Single(pools, p => p.SetKey.StartsWith('='));
        Assert.Equal($"={parentExecutor}", pinned.SetKey);
        Assert.Equal(1, pinned.Count);
    }

    [Fact]
    public async Task EvenFlowStaysCloseToIdealDistribution()
    {
        // исполнители закрывают заявки с одинаковой скоростью (FIFO с задержкой 30),
        // третий берёт только вклады — треть потока
        await _f.AddExecutor(1, qualification: 1m);
        await _f.AddExecutor(2, qualification: 2m);
        await _f.AddExecutor(3, qualification: 1m, subjects: ["вклад"]);
        for (var i = 1; i <= 900; i++)
        {
            var subject = i % 3 == 0 ? "вклад" : "кредит";
            await _f.Receive(i, attributes: BalancerFixture.DefaultOrder(subject: subject));
            if (i > 30)
            {
                await _f.ChangeStatus(i - 30, OrderStatus.Accept);
            }
        }

        var report = await _f.Analytics();

        var details = string.Join("; ", report.Executors.Select(e => $"#{e.Id}: {e.AssignedWeight} из {e.FairWeight}"));
        Assert.Equal(3, report.Fairness.ExecutorsMeasured);
        Assert.True(report.Fairness.MeanAbsDeviationPercent <= 2m, $"среднее {report.Fairness.MeanAbsDeviationPercent}%: {details}");
        // узкий специалист получает заявки только в свои моменты и чуть недобирает — дискретность онлайн-выбора
        Assert.True(report.Fairness.MaxAbsDeviationPercent <= 4m, $"максимум {report.Fairness.MaxAbsDeviationPercent}%: {details}");
        Assert.Equal(900, report.Kinds.Primary);
        Assert.Equal(870, report.Timeline.Sum(p => p.Closed));
    }

    [Fact]
    public async Task PreviewPredictsAssignmentWithoutChangingLoad()
    {
        await _f.AddExecutor(1);
        await _f.AddExecutor(2, qualification: 2m);
        await _f.Receive(1);

        var preview = await _f.Run(b => b.PreviewAsync(D, null,
            BalancerFixture.Attributes(BalancerFixture.DefaultOrder()), CancellationToken.None));
        var before = (_f.Store.OpenCount(1), _f.Store.OpenCount(2));
        var actual = await _f.Receive(2);

        Assert.Equal(preview.ChosenExecutorId, actual.ExecutorId);
        Assert.Equal((0, 1), before);
        Assert.Contains(preview.Candidates, c => c.Verdict == "chosen" && c.ExecutorId == actual.ExecutorId);
    }
}
