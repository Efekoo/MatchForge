using Matchmaking.Api.Data;
using Matchmaking.Api.Domain;

namespace Matchmaking.Api.Lobby;

public record MatchResult(
    Guid WinnerId, string WinnerName,
    int NewMmrA, int NewMmrB, int DeltaA, int DeltaB);

/// <summary>
/// Maç sonucunu Postgres'e yazar, Elo'yu günceller — döngüyü kapatan parça.
/// </summary>
public class MatchFinalizer(IServiceScopeFactory scopeFactory, ILogger<MatchFinalizer> logger)
{
    public async Task<MatchResult?> FinalizeAsync(LobbyState lobby, bool aWon)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pa = await db.Players.FindAsync(lobby.PlayerA);
        var pb = await db.Players.FindAsync(lobby.PlayerB);
        if (pa is null || pb is null)
        {
            logger.LogError("Finalize failed, players missing for lobby {Lobby}", lobby.LobbyId);
            return null;
        }

        var (deltaA, deltaB) = EloCalculator.Calculate(
            pa.Mmr, pb.Mmr, pa.GamesPlayed, pb.GamesPlayed, aWon);

        var oldA = pa.Mmr;
        var oldB = pb.Mmr;
        pa.Mmr += deltaA;
        pb.Mmr += deltaB;
        pa.GamesPlayed++;
        pb.GamesPlayed++;

        var match = new Match
        {
            PlayerAId = pa.Id,
            PlayerBId = pb.Id,
            WinnerId = aWon ? pa.Id : pb.Id,
            MmrDeltaA = deltaA,
            MmrDeltaB = deltaB,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(lobby.StartedAtMs).UtcDateTime,
            EndedAt = DateTime.UtcNow
        };
        db.Matches.Add(match);
        db.MmrHistory.Add(new MmrHistory { PlayerId = pa.Id, MatchId = match.Id, OldMmr = oldA, NewMmr = pa.Mmr });
        db.MmrHistory.Add(new MmrHistory { PlayerId = pb.Id, MatchId = match.Id, OldMmr = oldB, NewMmr = pb.Mmr });

        await db.SaveChangesAsync();

        logger.LogInformation("Match {Match} finished: winner {Winner}, MMR {A}: {OldA}->{NewA}, {B}: {OldB}->{NewB}",
            match.Id, aWon ? pa.Username : pb.Username,
            pa.Username, oldA, pa.Mmr, pb.Username, oldB, pb.Mmr);

        return new MatchResult(
            aWon ? pa.Id : pb.Id,
            aWon ? pa.Username : pb.Username,
            pa.Mmr, pb.Mmr, deltaA, deltaB);
    }
}
