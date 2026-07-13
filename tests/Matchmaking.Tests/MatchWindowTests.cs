using Matchmaking.Api.Domain;
using Xunit;

namespace Matchmaking.Tests;

public class MatchWindowTests
{
    [Theory]
    [InlineData(0, 50)]
    [InlineData(9.9, 50)]
    [InlineData(10, 100)]
    [InlineData(25, 150)]
    [InlineData(70, 400)]   // 50 + 7*50 = 400 (tam sınır)
    [InlineData(120, 400)]  // üst sınır aşılmaz
    public void Window_ExpandsWithWaitTime(double waitSeconds, int expected)
    {
        Assert.Equal(expected, MatchWindow.For(waitSeconds));
    }

    [Fact]
    public void CanMatch_RequiresBothWindowsToCover()
    {
        // Fark 120: A 15 sn beklemiş (±100) → A açısından sığmaz
        Assert.False(MatchWindow.CanMatch(1000, 15, 1120, 60));
        // İkisi de yeterince beklemişse eşleşir
        Assert.True(MatchWindow.CanMatch(1000, 25, 1120, 25));
    }

    [Fact]
    public void CanMatch_TightWindowAtStart()
    {
        Assert.True(MatchWindow.CanMatch(1000, 0, 1050, 0));   // fark 50 = sınır
        Assert.False(MatchWindow.CanMatch(1000, 0, 1051, 0));  // fark 51 > 50
    }
}
