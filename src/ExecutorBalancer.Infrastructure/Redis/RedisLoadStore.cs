using System.Globalization;
using ExecutorBalancer.Application.Balancing;
using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure.Redis;

/// <summary>
/// Нагрузка исполнителей в Redis. Выбор и увеличение счётчиков выполняются одним Lua-скриптом:
/// Redis исполняет скрипт целиком, пока другие команды ждут, поэтому два экземпляра сервиса
/// не могут одновременно отдать заявку одному и тому же «свободному» исполнителю.
/// </summary>
public sealed class RedisLoadStore(IConnectionMultiplexer redis) : ILoadStore
{
    private const int DailyTtlSeconds = 3 * 24 * 3600;
    private const int ClosedOrderTtlSeconds = 7 * 24 * 3600;

    // KEYS: 1 open-weight, 2 open-count, 3 daily, 4 active, 5 order
    // ARGV: 1 вес заявки, 2 reopen, 3 ttl дневного счётчика, далее тройки: id, квалификация, лимит (-1 — нет)
    private const string PickScript = """
        local state = redis.call('HGET', KEYS[5], 'state')
        if state == 'open' or (state and ARGV[2] ~= '1') then
          return {'existing', redis.call('HGET', KEYS[5], 'executor') or ''}
        end
        local w = tonumber(ARGV[1])
        local best, bestLoad, bestQ, bestDaily
        local report = {}
        for i = 4, #ARGV, 3 do
          local id = ARGV[i]
          local q = tonumber(ARGV[i + 1])
          local limit = tonumber(ARGV[i + 2])
          local daily = tonumber(redis.call('HGET', KEYS[3], id) or '0')
          local load = tonumber(redis.call('HGET', KEYS[1], id) or '0')
          if redis.call('HGET', KEYS[4], id) ~= '1' then
            report[#report + 1] = id .. '|inactive|' .. load .. '|' .. daily
          elseif limit >= 0 and daily >= limit then
            report[#report + 1] = id .. '|daily_limit_exceeded|' .. load .. '|' .. daily
          else
            report[#report + 1] = id .. '|eligible|' .. load .. '|' .. daily
            local better = false
            if best == nil then
              better = true
            else
              local lhs = (load + w) * bestQ
              local rhs = (bestLoad + w) * q
              if lhs < rhs then
                better = true
              elseif lhs == rhs then
                local dl = daily * bestQ
                local dr = bestDaily * q
                better = dl < dr or (dl == dr and tonumber(id) < tonumber(best))
              end
            end
            if better then
              best, bestLoad, bestQ, bestDaily = id, load, q, daily
            end
          end
        end
        if best == nil then
          return {'none', '', unpack(report)}
        end
        redis.call('HINCRBY', KEYS[1], best, w)
        redis.call('HINCRBY', KEYS[2], best, 1)
        redis.call('HINCRBY', KEYS[3], best, 1)
        redis.call('EXPIRE', KEYS[3], tonumber(ARGV[3]))
        redis.call('HSET', KEYS[5], 'executor', best, 'weight', w, 'state', 'open')
        redis.call('PERSIST', KEYS[5])
        return {'assigned', best, unpack(report)}
        """;

    // KEYS: 1 open-weight, 2 open-count, 3 order. ARGV: 1 новое состояние, 2 ttl, 3 удалить ключ (1/0)
    private const string ReleaseScript = """
        local state = redis.call('HGET', KEYS[3], 'state')
        if state ~= 'open' then
          return 0
        end
        local executor = redis.call('HGET', KEYS[3], 'executor')
        local w = tonumber(redis.call('HGET', KEYS[3], 'weight'))
        redis.call('HINCRBY', KEYS[1], executor, -w)
        redis.call('HINCRBY', KEYS[2], executor, -1)
        if ARGV[3] == '1' then
          redis.call('DEL', KEYS[3])
        else
          redis.call('HSET', KEYS[3], 'state', ARGV[1])
          if ARGV[1] ~= 'await' then
            redis.call('EXPIRE', KEYS[3], tonumber(ARGV[2]))
          end
        end
        return 1
        """;

    private IDatabase Db => redis.GetDatabase();

    public async Task<PickResult> PickAsync(PickRequest request, CancellationToken cancellationToken)
    {
        var args = new List<RedisValue>(3 + request.Candidates.Count * 3)
        {
            request.WeightMilli,
            request.Reopen ? 1 : 0,
            DailyTtlSeconds,
        };
        foreach (var candidate in request.Candidates)
        {
            args.Add(candidate.ExecutorId);
            args.Add(candidate.QualificationMilli);
            args.Add(candidate.DailyLimit ?? -1);
        }

        RedisKey[] keys =
        [
            RedisKeys.OpenWeight, RedisKeys.OpenCount, RedisKeys.Daily(request.Day), RedisKeys.Active,
            RedisKeys.Order(request.OrderId),
        ];
        var raw = (RedisResult[])(await Db.ScriptEvaluateAsync(PickScript, keys, args.ToArray()))!;

        var status = (string)raw[0]!;
        var executor = (string?)raw[1];
        var report = raw.Skip(2).Select(item => ParseReport((string)item!)).ToList();
        long? executorId = string.IsNullOrEmpty(executor) ? null : long.Parse(executor, CultureInfo.InvariantCulture);

        return status switch
        {
            "assigned" => new PickResult(PickStatus.Assigned, executorId, report),
            "existing" => new PickResult(PickStatus.AlreadyAssigned, executorId, report),
            _ => new PickResult(PickStatus.NoCandidate, null, report),
        };
    }

    public async Task<bool> ReleaseAsync(long orderId, LoadRelease release, CancellationToken cancellationToken)
    {
        var state = release switch
        {
            LoadRelease.Await => "await",
            LoadRelease.Close => "closed",
            _ => "rolled-back",
        };
        RedisKey[] keys = [RedisKeys.OpenWeight, RedisKeys.OpenCount, RedisKeys.Order(orderId)];
        RedisValue[] args = [state, ClosedOrderTtlSeconds, release == LoadRelease.Rollback ? 1 : 0];
        var result = await Db.ScriptEvaluateAsync(ReleaseScript, keys, args);
        return (int)result == 1;
    }

    public Task SetActiveAsync(long executorId, bool isActive, CancellationToken cancellationToken) =>
        Db.HashSetAsync(RedisKeys.Active, executorId, isActive ? 1 : 0);

    public async Task<long> GetConfigVersionAsync(CancellationToken cancellationToken)
    {
        var value = await Db.StringGetAsync(RedisKeys.ConfigVersion);
        return value.HasValue ? (long)value : 0;
    }

    public Task BumpConfigVersionAsync(CancellationToken cancellationToken) =>
        Db.StringIncrementAsync(RedisKeys.ConfigVersion);

    public async Task<IReadOnlyDictionary<long, ExecutorLoad>> GetLoadsAsync(DateOnly day,
        CancellationToken cancellationToken)
    {
        var db = Db;
        var weights = db.HashGetAllAsync(RedisKeys.OpenWeight);
        var counts = db.HashGetAllAsync(RedisKeys.OpenCount);
        var daily = db.HashGetAllAsync(RedisKeys.Daily(day));
        await Task.WhenAll(weights, counts, daily);

        var weightMap = ToMap(await weights);
        var countMap = ToMap(await counts);
        var dailyMap = ToMap(await daily);
        return weightMap.Keys.Union(countMap.Keys).Union(dailyMap.Keys).ToDictionary(
            id => id,
            id => new ExecutorLoad(weightMap.GetValueOrDefault(id), (int)countMap.GetValueOrDefault(id),
                (int)dailyMap.GetValueOrDefault(id)));
    }

    private static Dictionary<long, long> ToMap(HashEntry[] entries) =>
        entries.ToDictionary(e => long.Parse((string)e.Name!, CultureInfo.InvariantCulture), e => (long)e.Value);

    private static SlotReport ParseReport(string line)
    {
        var parts = line.Split('|');
        return new SlotReport(
            long.Parse(parts[0], CultureInfo.InvariantCulture),
            parts[1],
            long.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    }
}
