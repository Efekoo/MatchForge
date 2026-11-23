using Matchmaking.Stats.Consumers;
using Matchmaking.Stats.Data;
using Xunit;

namespace Matchmaking.Stats.Tests;

/// <summary>Seri hesabı: pozitif = üst üste galibiyet, negatif = üst üste mağlubiyet.</summary>
public class StreakTests
{
    private static readonly DateTime At = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Wins_BuildPositiveStreak_AndTrackBest()
    {
        var p = new PlayerStats();
        for (var i = 0; i < 3; i++) MatchCompletedConsumer.ApplyWin(p, "a", 1000 + i, At);

        Assert.Equal(3, p.Wins);
        Assert.Equal(3, p.CurrentStreak);
        Assert.Equal(3, p.BestStreak);
    }

    [Fact]
    public void Loss_ResetsWinStreak_ButKeepsBest()
    {
        var p = new PlayerStats();
        MatchCompletedConsumer.ApplyWin(p, "a", 1020, At);
        MatchCompletedConsumer.ApplyWin(p, "a", 1040, At);
        MatchCompletedConsumer.ApplyLoss(p, "a", 1020, forfeit: false, At);

        Assert.Equal(-1, p.CurrentStreak);
        Assert.Equal(2, p.BestStreak);
    }

    [Fact]
    public void ForfeitLoss_IsCountedSeparately()
    {
        var p = new PlayerStats();
        MatchCompletedConsumer.ApplyLoss(p, "a", 980, forfeit: true, At);
        MatchCompletedConsumer.ApplyLoss(p, "a", 960, forfeit: false, At);

        Assert.Equal(2, p.Losses);
        Assert.Equal(1, p.ForfeitLosses);
        Assert.Equal(-2, p.CurrentStreak);
        Assert.Equal(960, p.Mmr);
    }

    [Fact]
    public void OlderEvent_DoesNotOverwriteNewerMmr()
    {
        var p = new PlayerStats();
        MatchCompletedConsumer.ApplyWin(p, "a", 1039, At);              // yeni maç önce geldi
        MatchCompletedConsumer.ApplyWin(p, "a", 1020, At.AddMinutes(-5)); // eskisi geç geldi

        Assert.Equal(1039, p.Mmr);
        Assert.Equal(2, p.Wins); // sayaçlar yine de iki maçı da sayar
        Assert.Equal(At, p.LastMatchAt);
    }

    [Fact]
    public void Win_AfterLosses_StartsNewStreakAtOne()
    {
        var p = new PlayerStats();
        MatchCompletedConsumer.ApplyLoss(p, "a", 980, forfeit: false, At);
        MatchCompletedConsumer.ApplyWin(p, "a", 1000, At);

        Assert.Equal(1, p.CurrentStreak);
    }
}
