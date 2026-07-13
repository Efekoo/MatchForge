using Matchmaking.Api.Domain;
using Matchmaking.Api.Infrastructure;
using Matchmaking.Api.Queue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Matchmaking.Api.Lobby;

/// <summary>
/// Gerçek zamanlı maç akışı. Olaylar: MatchFound (matchmaker gönderir), OpponentReady,
/// MatchStarted, RoundResult, MatchEnded, OpponentDisconnected, OpponentReconnected, MatchCancelled.
///
/// - Hamle doğrulama tamamen sunucu tarafındadır — istemciye güvenilmez.
/// - Lobi içi yarışlar Redis dağıtık kilidi ile serileştirilir (replika bağımsız).
/// - Connection id geçicidir; oyuncu kimliği JWT'den gelir — reconnect'in temeli budur.
/// </summary>
[Authorize]
public class GameHub(
    LobbyStore lobbies,
    MatchFinalizer finalizer,
    RedisLockService locks,
    IConnectionMultiplexer redis,
    ILogger<GameHub> logger) : Hub
{
    public static readonly TimeSpan QueueGrace = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan LobbyGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LobbyLockTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LobbyLockWait = TimeSpan.FromSeconds(3);

    public static string QueueDiscKey(Guid playerId) => $"mm:queue:disc:{playerId}";
    public static string LobbyLockKey(string lobbyId) => $"lock:lobby:{lobbyId}";

    private Guid PlayerId => Guid.Parse(Context.UserIdentifier!);

    public override async Task OnConnectedAsync()
    {
        // Kuyruktayken kopan oyuncu grace period içinde geri döndü: marker'ı temizle
        await redis.GetDatabase().KeyDeleteAsync(QueueDiscKey(PlayerId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var playerId = PlayerId;
        var db = redis.GetDatabase();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Kuyruktaysa: hemen düşürme — 15 sn grace marker'ı koy (matchmaker süpürür)
        if (await db.SortedSetScoreAsync(QueueService.QueueKey, playerId.ToString()) is not null)
            await db.StringSetAsync(QueueDiscKey(playerId), nowMs, TimeSpan.FromMinutes(5));

        // Aktif lobideyse: kopma anını işaretle, rakibe haber ver (reaper 30 sn sonra hükmen bitirir)
        var lobbyId = await lobbies.GetPlayerLobbyIdAsync(playerId);
        if (lobbyId is not null)
        {
            var lobby = await lobbies.GetAsync(lobbyId);
            if (lobby is not null && lobby.State != "Finished")
            {
                var isA = playerId == lobby.PlayerA;
                await lobbies.SetFieldsAsync(lobbyId, (isA ? "discA" : "discB", nowMs));
                await Clients.Group(lobbyId).SendAsync("OpponentDisconnected",
                    new { graceSeconds = (int)LobbyGrace.TotalSeconds });
            }
        }

        logger.LogInformation("Player {Player} disconnected", playerId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinLobby(string lobbyId)
    {
        var token = await locks.AcquireAsync(LobbyLockKey(lobbyId), LobbyLockTtl, LobbyLockWait);
        if (token is null)
        {
            await Clients.Caller.SendAsync("Error", "Lobby is busy, try again.");
            return;
        }

        try
        {
            var lobby = await lobbies.GetAsync(lobbyId);
            if (lobby is null || !lobby.Contains(PlayerId))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found or you are not a member.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, lobbyId);

            var isA = PlayerId == lobby.PlayerA;

            // Reconnect: kopma marker'ı varsa temizle, rakibe haber ver
            if ((isA ? lobby.DiscA : lobby.DiscB) > 0)
            {
                await lobbies.SetFieldsAsync(lobbyId, (isA ? "discA" : "discB", 0));
                await Clients.OthersInGroup(lobbyId).SendAsync("OpponentReconnected");
            }

            // Maç zaten sürüyorsa güncel state'i gönder (yeni connection id, aynı kimlik)
            if (lobby.State == "InProgress")
            {
                await Clients.Caller.SendAsync("MatchStarted", new
                {
                    lobbyId,
                    playerA = lobby.NameA,
                    playerB = lobby.NameB,
                    round = lobby.Round,
                    scoreA = lobby.ScoreA,
                    scoreB = lobby.ScoreB
                });
                return;
            }

            await lobbies.SetFieldsAsync(lobbyId, (isA ? "readyA" : "readyB", 1));

            var bothReady = isA ? lobby.ReadyB : lobby.ReadyA; // diğeri zaten hazır mıydı?
            if (bothReady && lobby.State == "WaitingReady")
            {
                await lobbies.SetFieldsAsync(lobbyId, ("state", "InProgress"));
                await Clients.Group(lobbyId).SendAsync("MatchStarted", new
                {
                    lobbyId,
                    playerA = lobby.NameA,
                    playerB = lobby.NameB,
                    round = lobby.Round,
                    scoreA = lobby.ScoreA,
                    scoreB = lobby.ScoreB
                });
            }
            else
            {
                await Clients.OthersInGroup(lobbyId).SendAsync("OpponentReady");
                await Clients.Caller.SendAsync("WaitingForOpponent");
            }
        }
        finally
        {
            await locks.ReleaseAsync(LobbyLockKey(lobbyId), token);
        }
    }

    public async Task MakeMove(string lobbyId, string move)
    {
        if (!RpsGame.TryParse(move, out var parsedMove))
        {
            await Clients.Caller.SendAsync("Error", "Invalid move. Use rock, paper or scissors.");
            return;
        }

        var token = await locks.AcquireAsync(LobbyLockKey(lobbyId), LobbyLockTtl, LobbyLockWait);
        if (token is null)
        {
            await Clients.Caller.SendAsync("Error", "Lobby is busy, try again.");
            return;
        }

        try
        {
            var lobby = await lobbies.GetAsync(lobbyId);
            if (lobby is null || !lobby.Contains(PlayerId))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found or you are not a member.");
                return;
            }
            if (lobby.State != "InProgress")
            {
                await Clients.Caller.SendAsync("Error", "Match is not in progress.");
                return;
            }

            var isA = PlayerId == lobby.PlayerA;
            var myMove = isA ? lobby.MoveA : lobby.MoveB;
            if (myMove != "")
            {
                await Clients.Caller.SendAsync("Error", "You already moved this round.");
                return;
            }

            var moveStr = parsedMove.ToString().ToLowerInvariant();
            if (isA) lobby.MoveA = moveStr; else lobby.MoveB = moveStr;
            await lobbies.SetFieldsAsync(lobbyId, (isA ? "moveA" : "moveB", moveStr));
            await Clients.Caller.SendAsync("MoveAccepted", new { round = lobby.Round });

            if (lobby.MoveA == "" || lobby.MoveB == "")
            {
                await Clients.OthersInGroup(lobbyId).SendAsync("OpponentMoved");
                return;
            }

            await ResolveRoundAsync(lobby);
        }
        finally
        {
            await locks.ReleaseAsync(LobbyLockKey(lobbyId), token);
        }
    }

    private async Task ResolveRoundAsync(LobbyState lobby)
    {
        RpsGame.TryParse(lobby.MoveA, out var a);
        RpsGame.TryParse(lobby.MoveB, out var b);
        var outcome = RpsGame.ResolveRound(a, b);

        if (outcome == RoundOutcome.PlayerA) lobby.ScoreA++;
        else if (outcome == RoundOutcome.PlayerB) lobby.ScoreB++;
        // Beraberlik: skor değişmez, round tekrar oynanır.

        var finished = lobby.ScoreA == 2 || lobby.ScoreB == 2;

        await Clients.Group(lobby.LobbyId).SendAsync("RoundResult", new
        {
            round = lobby.Round,
            moveA = lobby.MoveA,
            moveB = lobby.MoveB,
            outcome = outcome.ToString(), // Draw | PlayerA | PlayerB
            scoreA = lobby.ScoreA,
            scoreB = lobby.ScoreB
        });

        if (!finished)
        {
            lobby.Round++;
            await lobbies.SetFieldsAsync(lobby.LobbyId,
                ("moveA", ""), ("moveB", ""),
                ("round", lobby.Round),
                ("scoreA", lobby.ScoreA), ("scoreB", lobby.ScoreB));
            return;
        }

        // Best of 3 bitti: Postgres'e yaz, Elo güncelle, lobiyi temizle.
        await lobbies.SetFieldsAsync(lobby.LobbyId, ("state", "Finished"));
        var aWon = lobby.ScoreA == 2;
        var result = await finalizer.FinalizeAsync(lobby, aWon);

        await Clients.Group(lobby.LobbyId).SendAsync("MatchEnded", new
        {
            winner = result?.WinnerName ?? (aWon ? lobby.NameA : lobby.NameB),
            scoreA = lobby.ScoreA,
            scoreB = lobby.ScoreB,
            forfeit = false,
            newMmrA = result?.NewMmrA,
            newMmrB = result?.NewMmrB,
            deltaA = result?.DeltaA,
            deltaB = result?.DeltaB
        });

        await lobbies.DeleteAsync(lobby.LobbyId, lobby.PlayerA, lobby.PlayerB);
    }
}
