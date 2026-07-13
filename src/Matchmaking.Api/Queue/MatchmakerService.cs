using Matchmaking.Api.Data;
using Matchmaking.Api.Domain;
using Matchmaking.Api.Infrastructure;
using Matchmaking.Api.Lobby;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Matchmaking.Api.Queue;

/// <summary>
/// Her 2 saniyede bir kuyruğu tarar; genişleyen MMR aralığına göre oyuncuları eşler.
///
/// Çoklu replika güvenliği:
/// - Eşleştirme turu "mm:matchmaker:lock" ile serileştirilir — her replika matchmaker
///   çalıştırır ama aynı anda yalnızca biri tur atar (yatay ölçeklenebilirlik bozulmaz).
/// - Her eşleştirmede oyuncu bazlı "mm:lock:{playerId}" kilidi alınır; kilidi alınamayan
///   oyuncu o turda atlanır → aynı oyuncu asla iki maça atanamaz.
/// </summary>
public class MatchmakerService(
    IConnectionMultiplexer redis,
    QueueService queue,
    LobbyStore lobbies,
    RedisLockService locks,
    IHubContext<GameHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<MatchmakerService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RoundLockTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PlayerLockTtl = TimeSpan.FromSeconds(10);

    public const string RoundLockKey = "mm:matchmaker:lock";
    public static string PlayerLockKey(Guid playerId) => $"mm:lock:{playerId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Matchmaker started, scanning every {Interval}s", ScanInterval.TotalSeconds);

        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunMatchingRoundAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Matchmaking round failed");
            }
        }
    }

    private async Task RunMatchingRoundAsync(CancellationToken ct)
    {
        // Tur kilidi: başka bir replika şu anda tur atıyorsa bu tick'i atla
        var roundToken = await locks.TryAcquireAsync(RoundLockKey, RoundLockTtl);
        if (roundToken is null) return;

        try
        {
            await SweepDisconnectedAsync();
            await PairPlayersAsync(ct);
        }
        finally
        {
            await locks.ReleaseAsync(RoundLockKey, roundToken);
        }
    }

    /// <summary>Kuyruktayken bağlantısı kopan ve grace period'u dolan oyuncuları düşürür.</summary>
    private async Task SweepDisconnectedAsync()
    {
        var db = redis.GetDatabase();
        var members = await db.SortedSetRangeByRankAsync(QueueService.QueueKey);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var member in members)
        {
            var playerId = Guid.Parse(member!);
            var disc = await db.StringGetAsync(GameHub.QueueDiscKey(playerId));
            if (disc.IsNullOrEmpty) continue;

            if (nowMs - (long)disc > GameHub.QueueGrace.TotalMilliseconds)
            {
                await queue.LeaveAsync(playerId);
                await db.KeyDeleteAsync(GameHub.QueueDiscKey(playerId));
                logger.LogInformation("Dropped disconnected player {Player} from queue (grace expired)", playerId);
            }
        }
    }

    private async Task PairPlayersAsync(CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var entries = await db.SortedSetRangeByRankWithScoresAsync(
            QueueService.QueueKey, 0, -1, Order.Ascending);
        if (entries.Length < 2) return;

        // MMR'a göre sıralı aday listesi + bekleme süreleri
        var candidates = new List<(Guid Id, int Mmr, double Wait)>(entries.Length);
        foreach (var e in entries)
        {
            var id = Guid.Parse(e.Element!);
            candidates.Add((id, (int)e.Score, await queue.GetWaitSecondsAsync(id)));
        }

        var matched = new bool[candidates.Count];
        for (var i = 0; i < candidates.Count - 1; i++)
        {
            if (matched[i]) continue;
            var a = candidates[i];

            // Oyuncu kilidi: alamazsak bu oyuncu başka bir işlemde — bu turda atla
            var tokenA = await locks.TryAcquireAsync(PlayerLockKey(a.Id), PlayerLockTtl);
            if (tokenA is null) continue;

            try
            {
                // Sıralı listede en yakın komşuyla eşleştirmeyi dene (greedy)
                for (var j = i + 1; j < candidates.Count; j++)
                {
                    if (matched[j]) continue;
                    var b = candidates[j];

                    if (!MatchWindow.CanMatch(a.Mmr, a.Wait, b.Mmr, b.Wait))
                        break; // sıralı liste: daha uzaktakiler de sığmaz

                    var tokenB = await locks.TryAcquireAsync(PlayerLockKey(b.Id), PlayerLockTtl);
                    if (tokenB is null) continue; // kilitli aday: sıradakine bak

                    try
                    {
                        matched[i] = matched[j] = true;
                        await CreateMatchAsync(a.Id, b.Id, ct);
                    }
                    finally
                    {
                        await locks.ReleaseAsync(PlayerLockKey(b.Id), tokenB);
                    }
                    break;
                }
            }
            finally
            {
                await locks.ReleaseAsync(PlayerLockKey(a.Id), tokenA);
            }
        }
    }

    private async Task CreateMatchAsync(Guid playerAId, Guid playerBId, CancellationToken ct)
    {
        // Kuyruktan çıkar; oyuncu bu arada ayrıldıysa eşleşmeyi iptal et
        var db = redis.GetDatabase();
        var removedA = await db.SortedSetRemoveAsync(QueueService.QueueKey, playerAId.ToString());
        var removedB = await db.SortedSetRemoveAsync(QueueService.QueueKey, playerBId.ToString());

        if (!removedA || !removedB)
        {
            // Biri kuyruktan çıkmış: diğerini geri koy
            if (removedA) await ReQueueAsync(playerAId, ct);
            if (removedB) await ReQueueAsync(playerBId, ct);
            return;
        }

        await db.KeyDeleteAsync(new RedisKey[]
        {
            QueueService.JoinedKey(playerAId), QueueService.JoinedKey(playerBId),
            GameHub.QueueDiscKey(playerAId), GameHub.QueueDiscKey(playerBId)
        });

        using var scope = scopeFactory.CreateScope();
        var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var players = await appDb.Players
            .Where(p => p.Id == playerAId || p.Id == playerBId)
            .ToDictionaryAsync(p => p.Id, ct);

        if (!players.TryGetValue(playerAId, out var pa) ||
            !players.TryGetValue(playerBId, out var pb))
        {
            logger.LogWarning("Matched players not found in DB: {A}, {B}", playerAId, playerBId);
            return;
        }

        var lobbyId = await lobbies.CreateAsync(pa.Id, pa.Username, pa.Mmr,
                                                pb.Id, pb.Username, pb.Mmr);

        logger.LogInformation("Match: {A} ({MmrA}) vs {B} ({MmrB}) -> lobby {Lobby}",
            pa.Username, pa.Mmr, pb.Username, pb.Mmr, lobbyId);

        await hub.Clients.User(pa.Id.ToString()).SendAsync("MatchFound",
            new { lobbyId, opponent = pb.Username, opponentMmr = pb.Mmr }, ct);
        await hub.Clients.User(pb.Id.ToString()).SendAsync("MatchFound",
            new { lobbyId, opponent = pa.Username, opponentMmr = pa.Mmr }, ct);
    }

    private async Task ReQueueAsync(Guid playerId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var player = await appDb.Players.FindAsync(new object[] { playerId }, ct);
        if (player is not null)
            await redis.GetDatabase().SortedSetAddAsync(
                QueueService.QueueKey, playerId.ToString(), player.Mmr);
    }
}
