namespace Matchmaking.Api.Domain;

/// <summary>
/// Genişleyen MMR aralığı: eşleşme kalitesi vs bekleme süresi trade-off'u.
/// 0–10 sn: ±50; her ek 10 sn: +50; üst sınır ±400.
/// </summary>
public static class MatchWindow
{
    public const int BaseWindow = 50;
    public const int StepSeconds = 10;
    public const int StepSize = 50;
    public const int MaxWindow = 400;

    public static int For(double waitSeconds)
    {
        if (waitSeconds < 0) waitSeconds = 0;
        var steps = (int)(waitSeconds / StepSeconds);
        var window = BaseWindow + steps * StepSize;
        return Math.Min(window, MaxWindow);
    }

    /// <summary>İki oyuncu ancak fark her ikisinin penceresine de sığıyorsa eşleşir.</summary>
    public static bool CanMatch(int mmrA, double waitA, int mmrB, double waitB)
    {
        var diff = Math.Abs(mmrA - mmrB);
        return diff <= For(waitA) && diff <= For(waitB);
    }
}
