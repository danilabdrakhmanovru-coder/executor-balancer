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
    // ARGV: 1 вес заявки, 2 reopen, 3 ttl дневного счётчика, 4 считать ли в суточную норму (1/0;
    //       0 — та же заявка вернулась к тому же исполнителю после доработки), далее четвёрки:
    //       id, квалификация, норма (суточный лимит, -1 — нет), потолок с режимом «больше нормы» (не меньше нормы)
    // Сначала выбираем среди тех, кто не набрал норму; сверх нормы (до потолка) — только если таких нет:
    // добровольцы получают излишки и не забирают заявки у коллег.
    // Ответ: статус, исполнитель, с какого момента заявка за ним (unix-секунды), далее строки отчёта.
    private const string PickScript = """
        local state = redis.call('HGET', KEYS[5], 'state')
        if state == 'open' or (state and ARGV[2] ~= '1') then
          local held = redis.call('HMGET', KEYS[5], 'executor', 'at')
          return {'existing', held[1] or '', held[2] or ''}
        end
        local w = tonumber(ARGV[1])
        local best = {}
        local report = {}
        local function consider(tier, id, load, q, daily)
          local b = best[tier]
          local better = false
          if b == nil then
            better = true
          else
            local lhs = (load + w) * b.q
            local rhs = (b.load + w) * q
            if lhs < rhs then
              better = true
            elseif lhs == rhs then
              local dl = daily * b.q
              local dr = b.daily * q
              better = dl < dr or (dl == dr and tonumber(id) < tonumber(b.id))
            end
          end
          if better then
            best[tier] = {id = id, load = load, q = q, daily = daily}
          end
        end
        for i = 5, #ARGV, 4 do
          local id = ARGV[i]
          local q = tonumber(ARGV[i + 1])
          local limit = tonumber(ARGV[i + 2])
          local cap = tonumber(ARGV[i + 3])
          local daily = tonumber(redis.call('HGET', KEYS[3], id) or '0')
          local load = tonumber(redis.call('HGET', KEYS[1], id) or '0')
          if redis.call('HGET', KEYS[4], id) ~= '1' then
            report[#report + 1] = id .. '|inactive|' .. load .. '|' .. daily
          elseif limit >= 0 and daily >= cap then
            report[#report + 1] = id .. '|daily_limit_exceeded|' .. load .. '|' .. daily
          elseif limit >= 0 and daily >= limit then
            report[#report + 1] = id .. '|over_norm|' .. load .. '|' .. daily
            consider(2, id, load, q, daily)
          else
            report[#report + 1] = id .. '|eligible|' .. load .. '|' .. daily
            consider(1, id, load, q, daily)
          end
        end
        local chosen = best[1] or best[2]
        if chosen == nil then
          return {'none', '', '', unpack(report)}
        end
        local winner = chosen.id
        local now = redis.call('TIME')[1]
        redis.call('HINCRBY', KEYS[1], winner, w)
        redis.call('HINCRBY', KEYS[2], winner, 1)
        local day = ''
        if ARGV[4] == '1' then
          redis.call('HINCRBY', KEYS[3], winner, 1)
          redis.call('EXPIRE', KEYS[3], tonumber(ARGV[3]))
          day = KEYS[3]
        end
        -- day: какой суточный счётчик увеличен (пусто — никакой) — откат должен вернуть именно его
        redis.call('HSET', KEYS[5], 'executor', winner, 'weight', w, 'state', 'open', 'at', now, 'day', day)
        redis.call('PERSIST', KEYS[5])
        return {'assigned', winner, now, unpack(report)}
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
          -- откат: решение не сохранено, поэтому место в суточном лимите тоже возвращается.
          -- Ключ счётчика записан в самой заявке; у всех ключей общий hash tag {eb}, слот тот же
          local day = redis.call('HGET', KEYS[3], 'day')
          if day and day ~= '' and tonumber(redis.call('HGET', day, executor) or '0') > 0 then
            redis.call('HINCRBY', day, executor, -1)
          end
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
        var args = new List<RedisValue>(4 + request.Candidates.Count * 4)
        {
            request.WeightMilli,
            request.Reopen ? 1 : 0,
            DailyTtlSeconds,
            request.CountsTowardDaily ? 1 : 0,
        };
        foreach (var candidate in request.Candidates)
        {
            args.Add(candidate.ExecutorId);
            args.Add(candidate.QualificationMilli);
            args.Add(candidate.DailyLimit ?? -1);
            args.Add(candidate.Cap);
        }

        RedisKey[] keys =
        [
            RedisKeys.OpenWeight, RedisKeys.OpenCount, RedisKeys.Daily(request.Day), RedisKeys.Active,
            RedisKeys.Order(request.OrderId),
        ];
        var raw = (RedisResult[])(await Db.ScriptEvaluateAsync(PickScript, keys, args.ToArray()))!;

        var status = (string)raw[0]!;
        var executor = (string?)raw[1];
        var heldAt = (string?)raw[2];
        var report = raw.Skip(3).Select(item => ParseReport((string)item!)).ToList();
        long? executorId = string.IsNullOrEmpty(executor) ? null : long.Parse(executor, CultureInfo.InvariantCulture);
        DateTimeOffset? heldSince = string.IsNullOrEmpty(heldAt)
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(long.Parse(heldAt, CultureInfo.InvariantCulture));

        return status switch
        {
            "assigned" => new PickResult(PickStatus.Assigned, executorId, report, heldSince),
            "existing" => new PickResult(PickStatus.AlreadyAssigned, executorId, report, heldSince),
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
