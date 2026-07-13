namespace Matchmaking.Api.Domain;

public enum RpsMove { Rock, Paper, Scissors }

public enum RoundOutcome { Draw, PlayerA, PlayerB }

/// <summary>Taş-kağıt-makas kuralları. Hamle doğrulama sunucu tarafındadır.</summary>
public static class RpsGame
{
    public static bool TryParse(string? input, out RpsMove move)
    {
        switch (input?.Trim().ToLowerInvariant())
        {
            case "rock": move = RpsMove.Rock; return true;
            case "paper": move = RpsMove.Paper; return true;
            case "scissors": move = RpsMove.Scissors; return true;
            default: move = default; return false;
        }
    }

    public static RoundOutcome ResolveRound(RpsMove a, RpsMove b)
    {
        if (a == b) return RoundOutcome.Draw;
        var aWins = (a == RpsMove.Rock && b == RpsMove.Scissors)
                 || (a == RpsMove.Paper && b == RpsMove.Rock)
                 || (a == RpsMove.Scissors && b == RpsMove.Paper);
        return aWins ? RoundOutcome.PlayerA : RoundOutcome.PlayerB;
    }
}
