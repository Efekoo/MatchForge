using Matchmaking.Api.Data;
using Matchmaking.Api.Domain;
using Matchmaking.Contracts;
using MassTransit;

namespace Matchmaking.Api.Lobby;

public record MatchResult(
    Guid WinnerId, string WinnerName,
    int NewMmrA, int NewMmrB, int DeltaA, int DeltaB);

/// <summary>
/// Maç sonucunu Postgres'e yazar, Elo'yu günceller — döngüyü kapatan parça.
/// Aynı transaction içinde <see cref="MatchCompleted"/> olayını outbox'a ekler.
/// </summary>
public class MatchFinalizer(IServiceScopeFactory scopeFactory, ILogger<MatchFinalizer> logger)
{
    public async Task<MatchResult?> FinalizeAsync(LobbyState lobby, bool aWon, bool forfeit = false)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Bus outbox açık olduğu için scope'taki IPublishEndpoint mesajı doğrudan RabbitMQ'ya
        // göndermez; aynı DbContext üzerinden outbox tablosuna yazar.
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

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

        var (winner, loser, winnerDelta, loserDelta) = aWon
            ? (pa, pb, deltaA, deltaB)
            : (pb, pa, deltaB, deltaA);

        await publisher.Publish(new MatchCompleted(
            match.Id,
            winner.Id, winner.Username, winner.Mmr, winnerDelta,
            loser.Id, loser.Username, loser.Mmr, loserDelta,
            forfeit, match.StartedAt, match.EndedAt));

        // Maç + Elo + MMR geçmişi + outbox mesajı tek SaveChanges = tek transaction
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
