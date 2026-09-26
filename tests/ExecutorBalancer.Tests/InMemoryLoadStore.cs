using ExecutorBalancer.Application.Balancing;

namespace ExecutorBalancer.Tests;

/// <summary>
/// Та же семантика, что у Lua-скрипта в RedisLoadStore, но в памяти процесса.
/// Нужна, чтобы проверять алгоритм без Redis; сам скрипт проверяется интеграционными тестами.
/// </summary>
internal sealed class InMemoryLoadStore : ILoadStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, long> _openWeight = new();
    private readonly Dictionary<long, int> _openCount = new();
    private readonly Dictionary<(DateOnly Day, long Executor), int> _daily = new();
    private readonly Dictionary<long, bool> _active = new();
    private readonly Dictionary<long, (long Executor, long Weight, string State)> _orders = new();
    private long _version;

    public Task<PickResult> PickAsync(PickRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_orders.TryGetValue(request.OrderId, out var existing) && (existing.State == "open" || !request.Reopen))
            {
                return Task.FromResult(new PickResult(PickStatus.AlreadyAssigned, existing.Executor, []));
            }

            var report = new List<SlotReport>();
            CandidateSlot? best = null;
            long bestLoad = 0;
            int bestDaily = 0;
            foreach (var slot in request.Candidates)
            {
                var load = _openWeight.GetValueOrDefault(slot.ExecutorId);
                var daily = _daily.GetValueOrDefault((request.Day, slot.ExecutorId));
                if (!_active.GetValueOrDefault(slot.ExecutorId))
                {
                    report.Add(new SlotReport(slot.ExecutorId, "inactive", load, daily));
                    continue;
                }

                if (slot.DailyLimit is { } limit && daily >= limit)
                {
                    report.Add(new SlotReport(slot.ExecutorId, "daily_limit_exceeded", load, daily));
                    continue;
                }

                report.Add(new SlotReport(slot.ExecutorId, "eligible", load, daily));
                if (best is null || LoadMath.IsBetter(request.WeightMilli,
                        load, slot.QualificationMilli, daily, slot.ExecutorId,
                        bestLoad, best.QualificationMilli, bestDaily, best.ExecutorId))
                {
                    best = slot;
                    bestLoad = load;
                    bestDaily = daily;
                }
            }

            if (best is null)
            {
                return Task.FromResult(new PickResult(PickStatus.NoCandidate, null, report));
            }

            var id = best.ExecutorId;
            _openWeight[id] = bestLoad + request.WeightMilli;
            _openCount[id] = _openCount.GetValueOrDefault(id) + 1;
            _daily[(request.Day, id)] = bestDaily + 1;
            _orders[request.OrderId] = (id, request.WeightMilli, "open");
            return Task.FromResult(new PickResult(PickStatus.Assigned, id, report));
        }
    }

    public Task<bool> ReleaseAsync(long orderId, LoadRelease release, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var order) || order.State != "open")
            {
                return Task.FromResult(false);
            }

            _openWeight[order.Executor] -= order.Weight;
            _openCount[order.Executor] -= 1;
            if (release == LoadRelease.Rollback)
            {
                _orders.Remove(orderId);
            }
            else
            {
                _orders[orderId] = order with { State = release == LoadRelease.Await ? "await" : "closed" };
            }

            return Task.FromResult(true);
        }
    }

    public Task SetActiveAsync(long executorId, bool isActive, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _active[executorId] = isActive;
        }

        return Task.CompletedTask;
    }

    public Task<long> GetConfigVersionAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Interlocked.Read(ref _version));

    public Task BumpConfigVersionAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _version);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<long, ExecutorLoad>> GetLoadsAsync(DateOnly day,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<long, ExecutorLoad> result = _active.Keys.ToDictionary(
                id => id,
                id => new ExecutorLoad(_openWeight.GetValueOrDefault(id), _openCount.GetValueOrDefault(id),
                    _daily.GetValueOrDefault((day, id))));
            return Task.FromResult(result);
        }
    }

    public int OpenCount(long executorId)
    {
        lock (_gate)
        {
            return _openCount.GetValueOrDefault(executorId);
        }
    }

    public long OpenWeight(long executorId)
    {
        lock (_gate)
        {
            return _openWeight.GetValueOrDefault(executorId);
        }
    }
}
