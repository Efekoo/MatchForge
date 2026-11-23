namespace Matchmaking.Contracts;

// Servisler arası sözleşmeler. MassTransit mesaj tipini namespace + tip adıyla
// tanımlar; bu yüzden hem yayınlayan (Api) hem tüketen (Stats) aynı assembly'yi kullanır.
// Olaylar geçmiş zamanda adlandırılır: "olmuş bir şey" bildirirler, komut değildirler.

/// <summary>Matchmaker iki oyuncuyu eşleştirip lobi açtığında yayınlanır.</summary>
public record MatchCreated(
    Guid LobbyId,
    Guid PlayerAId, string PlayerAName, int PlayerAMmr,
    Guid PlayerBId, string PlayerBName, int PlayerBMmr,
    DateTime CreatedAt);

/// <summary>
/// Maç sonucu Postgres'e yazıldığında yayınlanır. Elo burada zaten hesaplanmıştır;
/// tüketiciler sonucu yeniden hesaplamaz, sadece kendi okuma modellerini günceller.
/// </summary>
public record MatchCompleted(
    Guid MatchId,
    Guid WinnerId, string WinnerName, int WinnerNewMmr, int WinnerDelta,
    Guid LoserId, string LoserName, int LoserNewMmr, int LoserDelta,
    bool Forfeit,
    DateTime StartedAt,
    DateTime EndedAt);
