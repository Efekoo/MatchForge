using Matchmaking.Api.Domain;
using Xunit;

namespace Matchmaking.Tests;

public class RpsGameTests
{
    [Theory]
    [InlineData(RpsMove.Rock, RpsMove.Scissors, RoundOutcome.PlayerA)]
    [InlineData(RpsMove.Paper, RpsMove.Rock, RoundOutcome.PlayerA)]
    [InlineData(RpsMove.Scissors, RpsMove.Paper, RoundOutcome.PlayerA)]
    [InlineData(RpsMove.Scissors, RpsMove.Rock, RoundOutcome.PlayerB)]
    [InlineData(RpsMove.Rock, RpsMove.Rock, RoundOutcome.Draw)]
    [InlineData(RpsMove.Paper, RpsMove.Paper, RoundOutcome.Draw)]
    public void ResolveRound_FollowsRules(RpsMove a, RpsMove b, RoundOutcome expected)
    {
        Assert.Equal(expected, RpsGame.ResolveRound(a, b));
    }

    [Theory]
    [InlineData("rock", true)]
    [InlineData(" PAPER ", true)]
    [InlineData("Scissors", true)]
    [InlineData("lizard", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_ValidatesServerSide(string? input, bool valid)
    {
        Assert.Equal(valid, RpsGame.TryParse(input, out _));
    }
}
