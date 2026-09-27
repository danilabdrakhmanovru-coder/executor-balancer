using StackExchange.Redis;

namespace ExecutorBalancer.Infrastructure.Redis;

/// <summary>
/// Все ключи с одним hash tag {eb}: в Redis Cluster они окажутся в одном слоте,
/// и Lua-скрипт сможет работать с ними атомарно.
/// </summary>
internal static class RedisKeys
{
    public static readonly RedisKey OpenWeight = "{eb}:open-weight";
    public static readonly RedisKey OpenCount = "{eb}:open-count";
    public static readonly RedisKey Active = "{eb}:active";
    public static readonly RedisKey ConfigVersion = "{eb}:config-version";
    public static readonly RedisKey StateReady = "{eb}:state-ready";
    public static readonly RedisKey RebuildLock = "{eb}:rebuild-lock";

    public static RedisKey Daily(DateOnly day) => $"{{eb}}:daily:{day:yyyyMMdd}";

    /// <summary>Вес, полученный каждым исполнителем за час (номер часа — unix-время / 3600).</summary>
    public static RedisKey HourWeight(long hour) => $"{{eb}}:hour-weight:{hour}";

    public static RedisKey Order(long orderId) => $"{{eb}}:order:{orderId}";
}
