using Matchmaking.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace Matchmaking.Api.Lobby;

/// <summary>
/// Aktif lobileri periyodik tarar:
/// - 30 sn'den uzun süre kopuk kalan oyuncu hükmen mağlup olur (maç sürüyorsa).
/// - İki taraf da kopmuşsa veya maç hiç başlamamışsa lobi sonuçsuz kapatılır.
/// Çoklu replikada tek reaper çalışsın diye tarama Redis kilidi ile serileştirilir.
/// </summary>
public class LobbyReaperService(
    LobbyStore lobbies,
    MatchFinalizer finalizer,
    RedisLockService locks,
    IHubContext<GameHub> hub,
    ILogger<LobbyReaperService> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReaperLockTtl = TimeSpan.FromSeconds(15);
    private const string ReaperLockKey = "lobby:reaper:lock";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Lobby reaper started, scanning every {Interval}s", ScanInterval.TotalSeconds);

        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var token = await locks.TryAcquireAsync(ReaperLockKey, ReaperLockTtl);
            if (token is null) continue; // başka replika tarıyor

            try
            {
                await ReapAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lobby reap failed");
            }
            finally
            {
                await locks.ReleaseAsync(ReaperLockKey, token);
            }
        }
    }

    private async Task ReapAsync()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var graceMs = GameHub.LobbyGrace.TotalMilliseconds;

        foreach (var lobbyId in await lobbies.GetActiveLobbyIdsAsync())
        {
            var lobby = await lobbies.GetAsync(lobbyId);
            if (lobby is null || lobby.State == "Finished")
            {
                await lobbies.RemoveFromActiveAsync(lobbyId); // TTL ile silinmiş / bitmiş
                continue;
            }

            var aGone = lobby.DiscA > 0 && nowMs - lobby.DiscA > graceMs;
            var bGone = lobby.DiscB > 0 && nowMs - lobby.DiscB > graceMs;
            if (!aGone && !bGone) continue;

            // Yarış önleme: reaper da lobi kilidini alır (hamle işlenirken silmeyelim)
            var lockToken = await locks.TryAcquireAsync(GameHub.LobbyLockKey(lobbyId), TimeSpan.FromSeconds(5));
            if (lockToken is null) continue; // sonraki turda tekrar bakılır

            try
            {
                if (aGone && bGone)
                {
                    logger.LogInformation("Lobby {Lobby}: both players gone, closing without result", lobbyId);
                    await lobbies.DeleteAsync(lobbyId, lobby.PlayerA, lobby.PlayerB);
                    continue;
                }

                if (lobby.State == "InProgress")
                {
                    // Hükmen mağlubiyet: bağlı kalan oyuncu kazanır
                    var aWon = bGone;
                    await lobbies.SetFieldsAsync(lobbyId, ("state", "Finished"));
                    var result = await finalizer.FinalizeAsync(lobby, aWon);

                    logger.LogInformation("Lobby {Lobby}: {Loser} forfeited (disconnect > {Grace}s)",
                        lobbyId, aWon ? lobby.NameB : lobby.NameA, GameHub.LobbyGrace.TotalSeconds);

                    await hub.Clients.Group(lobbyId).SendAsync("MatchEnded", new
                    {
                        winner = result?.WinnerName ?? (aWon ? lobby.NameA : lobby.NameB),
                        scoreA = lobby.ScoreA,
                        scoreB = lobby.ScoreB,
                        forfeit = true,
                        newMmrA = result?.NewMmrA,
                        newMmrB = result?.NewMmrB,
                        deltaA = result?.DeltaA,
                        deltaB = result?.DeltaB
                    });
                }
                else
                {
                    // Maç hiç başlamadı: sonuçsuz iptal
                    logger.LogInformation("Lobby {Lobby}: cancelled before start (opponent never joined)", lobbyId);
                    await hub.Clients.Group(lobbyId).SendAsync("MatchCancelled",
                        new { reason = "Opponent did not join." });
                }

                await lobbies.DeleteAsync(lobbyId, lobby.PlayerA, lobby.PlayerB);
            }
            finally
            {
                await locks.ReleaseAsync(GameHub.LobbyLockKey(lobbyId), lockToken);
            }
        }
    }
}
