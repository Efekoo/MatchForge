using StackExchange.Redis;

namespace Matchmaking.Api.Infrastructure;

/// <summary>
/// Redis SETNX tabanlı dağıtık kilit. Release, token karşılaştıran Lua script'i ile yapılır —
/// böylece TTL dolup kilidi başkası aldıysa yanlışlıkla onun kilidini silmeyiz.
/// </summary>
public class RedisLockService(IConnectionMultiplexer redis)
{
    private const string ReleaseScript = """
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end
        """;

    /// <summary>Kilidi dener; alamazsa null döner (beklemez).</summary>
    public async Task<string?> TryAcquireAsync(string key, TimeSpan ttl)
    {
        var token = Guid.NewGuid().ToString("N");
        var acquired = await redis.GetDatabase()
            .StringSetAsync(key, token, ttl, When.NotExists);
        return acquired ? token : null;
    }

    /// <summary>Kilidi maxWait süresince 50 ms aralıklarla dener.</summary>
    public async Task<string?> AcquireAsync(string key, TimeSpan ttl, TimeSpan maxWait)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (true)
        {
            var token = await TryAcquireAsync(key, ttl);
            if (token is not null) return token;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(50);
        }
    }

    public Task ReleaseAsync(string key, string token) =>
        redis.GetDatabase().ScriptEvaluateAsync(
            ReleaseScript, new RedisKey[] { key }, new RedisValue[] { token });
}
