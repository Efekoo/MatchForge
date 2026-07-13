using Matchmaking.Api.Domain;
using Xunit;

namespace Matchmaking.Tests;

public class EloCalculatorTests
{
    [Fact]
    public void EqualRatings_WinnerGainsHalfK()
    {
        // İki oturmuş oyuncu (K=20), eşit rating: beklenen skor 0.5 → delta ±10
        var (deltaA, deltaB) = EloCalculator.Calculate(1000, 1000, 50, 50, aWon: true);
        Assert.Equal(10, deltaA);
        Assert.Equal(-10, deltaB);
    }

    [Fact]
    public void Underdog_GainsMoreWhenWinning()
    {
        // 800'lük oyuncu 1200'lük oyuncuyu yenerse büyük kazanç
        var (deltaUnderdog, deltaFavorite) = EloCalculator.Calculate(800, 1200, 50, 50, aWon: true);
        Assert.True(deltaUnderdog > 15); // beklenen skor ~0.09 → ~+18
        Assert.True(deltaFavorite < -15);
    }

    [Fact]
    public void Favorite_GainsLittleWhenWinning()
    {
        var (deltaFavorite, deltaUnderdog) = EloCalculator.Calculate(1200, 800, 50, 50, aWon: true);
        Assert.InRange(deltaFavorite, 1, 5); // beklenen skor ~0.91 → ~+2
        Assert.InRange(deltaUnderdog, -5, -1);
    }

    [Fact]
    public void NewPlayer_UsesHigherKFactor()
    {
        Assert.Equal(EloCalculator.NewPlayerK, EloCalculator.GetKFactor(0));
        Assert.Equal(EloCalculator.NewPlayerK, EloCalculator.GetKFactor(19));
        Assert.Equal(EloCalculator.EstablishedK, EloCalculator.GetKFactor(20));
    }

    [Fact]
    public void NewPlayer_ConvergesFasterThanEstablished()
    {
        var (deltaNew, _) = EloCalculator.Calculate(1000, 1000, 0, 50, aWon: true);
        var (deltaEst, _) = EloCalculator.Calculate(1000, 1000, 50, 50, aWon: true);
        Assert.True(deltaNew > deltaEst); // 20 > 10
    }

    [Fact]
    public void ExpectedScore_IsSymmetric()
    {
        var ea = EloCalculator.ExpectedScore(1100, 900);
        var eb = EloCalculator.ExpectedScore(900, 1100);
        Assert.Equal(1.0, ea + eb, precision: 10);
    }

    [Fact]
    public void ZeroSum_WhenKFactorsEqual()
    {
        var (deltaA, deltaB) = EloCalculator.Calculate(1050, 970, 30, 40, aWon: false);
        Assert.Equal(0, deltaA + deltaB);
    }
}
