using Matchmaking.Api.Data;
using Matchmaking.Api.Domain;
using Matchmaking.Api.Lobby;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Matchmaking.Api.Queue;

/// <summary>
/// Her 2 saniyede bir kuyruğu tarar; genişleyen MMR aralığına göre oyuncuları eşler.
/// MVP: tek instance çalışır. v1.1'de eşleştirme turu Redis kilidi ile serileştirilecek.
/// </summary>
public class MatchmakerService(
    IConnectionMultiplexer redis,
    QueueService queue,
    LobbyStore lobbies,
    IHubContext<GameHub> hub,
    IServiceScopeFactory scopeFactory,
    ILogger<MatchmakerService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

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

            // Sıralı listede en yakın komşuyla eşleştirmeyi dene (greedy)
            for (var j = i + 1; j < candidates.Count; j++)
            {
                if (matched[j]) continue;
                var b = candidates[j];

                if (!MatchWindow.CanMatch(a.Mmr, a.Wait, b.Mmr, b.Wait))
                    break; // sıralı liste: daha uzaktakiler de sığmaz

                matched[i] = matched[j] = true;
                await CreateMatchAsync(a.Id, b.Id, ct);
                break;
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
            QueueService.JoinedKey(playerAId), QueueService.JoinedKey(playerBId)
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
