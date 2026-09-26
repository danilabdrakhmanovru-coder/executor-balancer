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
        await _f.AddExecutor(1, subjects: ["deposit"]);
        await _f.AddExecutor(2, subjects: ["credit"]);

        var result = await _f.Receive(10, attributes: BalancerFixture.DefaultOrder(subject: "credit"));

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

    [Fact]
    public async Task ParentExecutorThatNoLongerMatchesIsSkipped()
    {
        await _f.AddExecutor(1);
        await _f.Receive(1);
        await _f.AddExecutor(2);
        await _f.AddExecutor(1, subjects: ["deposit"]);

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
        await _f.AddExecutor(1, subjects: ["deposit"]);

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
        await _f.AddExecutor(1, subjects: ["deposit"]);
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
}
