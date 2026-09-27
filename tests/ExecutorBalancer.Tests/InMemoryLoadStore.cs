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
    private readonly Dictionary<(long Hour, long Executor), long> _hourWeight = new();
    private readonly Dictionary<long, bool> _active = new();
    private readonly Dictionary<long, (long Executor, long Weight, string State, DateOnly Day, DateTimeOffset At, bool Counted, long Hour)> _orders = new();
    private long _version;

    public Task<PickResult> PickAsync(PickRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_orders.TryGetValue(request.OrderId, out var existing) && (existing.State == "open" || !request.Reopen))
            {
                return Task.FromResult(new PickResult(PickStatus.AlreadyAssigned, existing.Executor, [], existing.At));
            }

            var report = new List<SlotReport>();
            // ярус 0 — норма не набрана, ярус 1 — сверх нормы (только если в ярусе 0 никого)
            var best = new (CandidateSlot Slot, long Load, int Daily, long Hour)?[2];
            foreach (var slot in request.Candidates)
            {
                var load = _openWeight.GetValueOrDefault(slot.ExecutorId);
                var daily = _daily.GetValueOrDefault((request.Day, slot.ExecutorId));
                var hour = _hourWeight.GetValueOrDefault((request.Hour, slot.ExecutorId));
                if (!_active.GetValueOrDefault(slot.ExecutorId))
                {
                    report.Add(new SlotReport(slot.ExecutorId, "inactive", load, daily, hour));
                    continue;
                }

                if (slot.DailyLimit is not null && daily >= slot.Cap)
                {
                    report.Add(new SlotReport(slot.ExecutorId, "daily_limit_exceeded", load, daily, hour));
                    continue;
                }

                var tier = slot.DailyLimit is { } limit && daily >= limit ? 1 : 0;
                report.Add(new SlotReport(slot.ExecutorId, tier == 0 ? "eligible" : "over_norm", load, daily, hour));
                if (best[tier] is not { } b || LoadMath.IsBetter(request.WeightMilli,
                        hour, load, slot.QualificationMilli, daily, slot.ExecutorId,
                        b.Hour, b.Load, b.Slot.QualificationMilli, b.Daily, b.Slot.ExecutorId))
                {
                    best[tier] = (slot, load, daily, hour);
                }
            }

            if ((best[0] ?? best[1]) is not { } winner)
            {
                return Task.FromResult(new PickResult(PickStatus.NoCandidate, null, report));
            }

            var (bestSlot, bestLoad, bestDaily, bestHour) = winner;
            var id = bestSlot.ExecutorId;
            _openWeight[id] = bestLoad + request.WeightMilli;
            _openCount[id] = _openCount.GetValueOrDefault(id) + 1;
            _hourWeight[(request.Hour, id)] = bestHour + request.WeightMilli;
            if (request.CountsTowardDaily)
            {
                _daily[(request.Day, id)] = bestDaily + 1;
            }

            var now = DateTimeOffset.UtcNow;
            _orders[request.OrderId] = (id, request.WeightMilli, "open", request.Day, now, request.CountsTowardDaily, request.Hour);
            return Task.FromResult(new PickResult(PickStatus.Assigned, id, report, now));
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
                var key = (order.Day, order.Executor);
                if (order.Counted && _daily.GetValueOrDefault(key) > 0)
                {
                    _daily[key] -= 1;
                }

                var hourKey = (order.Hour, order.Executor);
                if (_hourWeight.GetValueOrDefault(hourKey) >= order.Weight)
                {
                    _hourWeight[hourKey] -= order.Weight;
                }

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

    public Task<IReadOnlyDictionary<long, ExecutorLoad>> GetLoadsAsync(DateOnly day, long hour,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<long, ExecutorLoad> result = _active.Keys.ToDictionary(
                id => id,
                id => new ExecutorLoad(_openWeight.GetValueOrDefault(id), _openCount.GetValueOrDefault(id),
                    _daily.GetValueOrDefault((day, id)), _hourWeight.GetValueOrDefault((hour, id))));
            return Task.FromResult(result);
        }
    }

    /// <summary>Сдвигает момент закрепления заявки в прошлое — как будто процесс упал давно.</summary>
    public void Backdate(long orderId, TimeSpan age)
    {
        lock (_gate)
        {
            var order = _orders[orderId];
            _orders[orderId] = order with { At = order.At - age };
        }
    }

    public int AssignedOn(DateOnly day, long executorId)
    {
        lock (_gate)
        {
            return _daily.GetValueOrDefault((day, executorId));
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
