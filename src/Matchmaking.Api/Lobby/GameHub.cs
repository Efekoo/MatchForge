using System.Collections.Concurrent;
using Matchmaking.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Matchmaking.Api.Lobby;

/// <summary>
/// Gerçek zamanlı maç akışı. Olaylar: MatchFound (matchmaker gönderir),
/// OpponentReady, MatchStarted, RoundResult, MatchEnded.
/// Hamle doğrulama tamamen sunucu tarafındadır — istemciye güvenilmez.
/// </summary>
[Authorize]
public class GameHub(LobbyStore lobbies, MatchFinalizer finalizer, ILogger<GameHub> logger) : Hub
{
    // MVP: lobi içi yarışları tek instance'ta serileştirmek için in-process kilit.
    // v1.1'de Redis dağıtık kilidi ile değiştirilecek (çoklu replika için).
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LobbyLocks = new();

    private Guid PlayerId => Guid.Parse(Context.UserIdentifier!);

    private static SemaphoreSlim LockFor(string lobbyId) =>
        LobbyLocks.GetOrAdd(lobbyId, _ => new SemaphoreSlim(1, 1));

    public async Task JoinLobby(string lobbyId)
    {
        var gate = LockFor(lobbyId);
        await gate.WaitAsync();
        try
        {
            var lobby = await lobbies.GetAsync(lobbyId);
            if (lobby is null || !lobby.Contains(PlayerId))
            {
                await Clients.Caller.SendAsync("Error", "Lobby not found or you are not a member.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, lobbyId);

            // Reconnect: maç zaten sürüyorsa güncel state'i gönder (yeni connection id, aynı kimlik)
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

            var isA = PlayerId == lobby.PlayerA;
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
            gate.Release();
        }
    }

    public async Task MakeMove(string lobbyId, string move)
    {
        if (!RpsGame.TryParse(move, out var parsedMove))
        {
            await Clients.Caller.SendAsync("Error", "Invalid move. Use rock, paper or scissors.");
            return;
        }

        var gate = LockFor(lobbyId);
        await gate.WaitAsync();
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
            gate.Release();
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
            newMmrA = result?.NewMmrA,
            newMmrB = result?.NewMmrB,
            deltaA = result?.DeltaA,
            deltaB = result?.DeltaB
        });

        await lobbies.DeleteAsync(lobby.LobbyId, lobby.PlayerA, lobby.PlayerB);
        LobbyLocks.TryRemove(lobby.LobbyId, out _);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // MVP: lobi TTL ile kendini temizler. v1.1: grace period + reconnect akışı.
        logger.LogInformation("Player {Player} disconnected", Context.UserIdentifier);
        return base.OnDisconnectedAsync(exception);
    }
}
