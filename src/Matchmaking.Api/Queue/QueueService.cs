using Matchmaking.Api.Domain;
using StackExchange.Redis;

namespace Matchmaking.Api.Queue;

public record QueueStatus(bool InQueue, double WaitSeconds, int CurrentWindow, long QueueLength);

public class QueueService(IConnectionMultiplexer redis)
{
    public const string QueueKey = "mm:queue";
    public static string JoinedKey(Guid playerId) => $"mm:queue:joined:{playerId}";
    public static string PlayerLobbyKey(Guid playerId) => $"player:lobby:{playerId}";

    private IDatabase Db => redis.GetDatabase();

    public enum JoinResult { Joined, AlreadyQueued, AlreadyInMatch }

    /// <summary>Idempotent: oyuncu zaten kuyruktaysa ikinci istek reddedilir.</summary>
    public async Task<JoinResult> JoinAsync(Guid playerId, int mmr)
    {
        if (await Db.KeyExistsAsync(PlayerLobbyKey(playerId)))
            return JoinResult.AlreadyInMatch;

        var added = await Db.SortedSetAddAsync(QueueKey, playerId.ToString(), mmr, When.NotExists);
        if (!added)
            return JoinResult.AlreadyQueued;

        await Db.StringSetAsync(JoinedKey(playerId),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            expiry: TimeSpan.FromMinutes(30));
        return JoinResult.Joined;
    }

    public async Task<bool> LeaveAsync(Guid playerId)
    {
        var removed = await Db.SortedSetRemoveAsync(QueueKey, playerId.ToString());
        await Db.KeyDeleteAsync(JoinedKey(playerId));
        return removed;
    }

    public async Task<QueueStatus> GetStatusAsync(Guid playerId)
    {
        var score = await Db.SortedSetScoreAsync(QueueKey, playerId.ToString());
        var length = await Db.SortedSetLengthAsync(QueueKey);
        if (score is null)
            return new QueueStatus(false, 0, 0, length);

        var wait = await GetWaitSecondsAsync(playerId);
        return new QueueStatus(true, Math.Round(wait, 1), MatchWindow.For(wait), length);
    }

    public async Task<double> GetWaitSecondsAsync(Guid playerId)
    {
        var joined = await Db.StringGetAsync(JoinedKey(playerId));
        if (joined.IsNullOrEmpty) return 0;
        var joinedMs = (long)joined;
        return (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - joinedMs) / 1000.0;
    }

    /// <summary>Eşleşme sonrası temizlik (matchmaker çağırır).</summary>
    public async Task RemovePairAsync(Guid a, Guid b)
    {
        var db = Db;
        await db.SortedSetRemoveAsync(QueueKey, new RedisValue[] { a.ToString(), b.ToString() });
        await db.KeyDeleteAsync(new RedisKey[] { JoinedKey(a), JoinedKey(b) });
    }
}
