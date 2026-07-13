using StackExchange.Redis;

namespace Matchmaking.Api.Lobby;

public class LobbyState
{
    public string LobbyId { get; init; } = null!;
    public Guid PlayerA { get; init; }
    public Guid PlayerB { get; init; }
    public string NameA { get; init; } = "";
    public string NameB { get; init; } = "";
    public int MmrA { get; init; }
    public int MmrB { get; init; }
    public int ScoreA { get; set; }
    public int ScoreB { get; set; }
    public int Round { get; set; }
    public string MoveA { get; set; } = "";
    public string MoveB { get; set; } = "";
    public bool ReadyA { get; set; }
    public bool ReadyB { get; set; }
    public string State { get; set; } = "WaitingReady"; // WaitingReady | InProgress | Finished
    public long StartedAtMs { get; init; }

    public bool Contains(Guid playerId) => playerId == PlayerA || playerId == PlayerB;
}

/// <summary>
/// Lobi state'i Redis'te hash olarak, TTL ile tutulur — terk edilen lobiler kendini temizler.
/// </summary>
public class LobbyStore(IConnectionMultiplexer redis)
{
    public static readonly TimeSpan LobbyTtl = TimeSpan.FromMinutes(10);
    private static string LobbyKey(string lobbyId) => $"lobby:{lobbyId}";
    private static string PlayerLobbyKey(Guid playerId) => $"player:lobby:{playerId}";

    private IDatabase Db => redis.GetDatabase();

    public async Task<string> CreateAsync(Guid playerA, string nameA, int mmrA,
                                          Guid playerB, string nameB, int mmrB)
    {
        var lobbyId = Guid.NewGuid().ToString("N");
        var entries = new HashEntry[]
        {
            new("playerA", playerA.ToString()), new("playerB", playerB.ToString()),
            new("nameA", nameA), new("nameB", nameB),
            new("mmrA", mmrA), new("mmrB", mmrB),
            new("scoreA", 0), new("scoreB", 0),
            new("round", 1),
            new("moveA", ""), new("moveB", ""),
            new("readyA", 0), new("readyB", 0),
            new("state", "WaitingReady"),
            new("startedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        };

        var db = Db;
        await db.HashSetAsync(LobbyKey(lobbyId), entries);
        await db.KeyExpireAsync(LobbyKey(lobbyId), LobbyTtl);
        await db.StringSetAsync(PlayerLobbyKey(playerA), lobbyId, LobbyTtl);
        await db.StringSetAsync(PlayerLobbyKey(playerB), lobbyId, LobbyTtl);
        return lobbyId;
    }

    public async Task<LobbyState?> GetAsync(string lobbyId)
    {
        var entries = await Db.HashGetAllAsync(LobbyKey(lobbyId));
        if (entries.Length == 0) return null;

        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        return new LobbyState
        {
            LobbyId = lobbyId,
            PlayerA = Guid.Parse(map["playerA"]!),
            PlayerB = Guid.Parse(map["playerB"]!),
            NameA = map["nameA"]!,
            NameB = map["nameB"]!,
            MmrA = (int)map["mmrA"],
            MmrB = (int)map["mmrB"],
            ScoreA = (int)map["scoreA"],
            ScoreB = (int)map["scoreB"],
            Round = (int)map["round"],
            MoveA = map["moveA"]!,
            MoveB = map["moveB"]!,
            ReadyA = (int)map["readyA"] == 1,
            ReadyB = (int)map["readyB"] == 1,
            State = map["state"]!,
            StartedAtMs = (long)map["startedAtMs"]
        };
    }

    public async Task<string?> GetPlayerLobbyIdAsync(Guid playerId)
    {
        var value = await Db.StringGetAsync(PlayerLobbyKey(playerId));
        return value.IsNullOrEmpty ? null : (string?)value;
    }

    public Task SetFieldsAsync(string lobbyId, params (string Field, RedisValue Value)[] fields) =>
        Db.HashSetAsync(LobbyKey(lobbyId),
            fields.Select(f => new HashEntry(f.Field, f.Value)).ToArray());

    public async Task DeleteAsync(string lobbyId, Guid playerA, Guid playerB)
    {
        await Db.KeyDeleteAsync(new RedisKey[]
        {
            LobbyKey(lobbyId), PlayerLobbyKey(playerA), PlayerLobbyKey(playerB)
        });
    }
}
