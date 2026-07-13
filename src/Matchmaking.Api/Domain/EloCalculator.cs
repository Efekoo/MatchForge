namespace Matchmaking.Api.Domain;

/// <summary>
/// Kendi Elo implementasyonumuz (kütüphane yok).
/// E = 1 / (1 + 10^((Rb - Ra) / 400)),  Ra' = Ra + K * (S - E)
/// </summary>
public static class EloCalculator
{
    public const int NewPlayerK = 40;
    public const int EstablishedK = 20;
    public const int PlacementGames = 20;

    /// <summary>Yeni oyuncular hızlı yakınsasın diye ilk 20 maçta K yüksektir.</summary>
    public static int GetKFactor(int gamesPlayed) =>
        gamesPlayed < PlacementGames ? NewPlayerK : EstablishedK;

    public static double ExpectedScore(int ratingA, int ratingB) =>
        1.0 / (1.0 + Math.Pow(10, (ratingB - ratingA) / 400.0));

    /// <summary>
    /// aWon=true ise A kazanmıştır. Delta'lar yuvarlanmış tam sayıdır.
    /// </summary>
    public static (int DeltaA, int DeltaB) Calculate(
        int ratingA, int ratingB, int gamesPlayedA, int gamesPlayedB, bool aWon)
    {
        var expectedA = ExpectedScore(ratingA, ratingB);
        var expectedB = 1.0 - expectedA;
        var scoreA = aWon ? 1.0 : 0.0;
        var scoreB = 1.0 - scoreA;

        var deltaA = (int)Math.Round(GetKFactor(gamesPlayedA) * (scoreA - expectedA));
        var deltaB = (int)Math.Round(GetKFactor(gamesPlayedB) * (scoreB - expectedB));
        return (deltaA, deltaB);
    }
}
