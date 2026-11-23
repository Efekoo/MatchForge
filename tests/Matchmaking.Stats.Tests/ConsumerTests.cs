using Matchmaking.Contracts;
using Matchmaking.Stats.Consumers;
using Matchmaking.Stats.Data;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Matchmaking.Stats.Tests;

/// <summary>
/// MassTransit in-memory test harness: broker gerektirmeden consumer'ları gerçek
/// pipeline üzerinden çalıştırır. Docker'sız, hızlı geri bildirim için.
/// </summary>
public class ConsumerTests
{
    private static ServiceProvider BuildProvider()
    {
        var dbName = Guid.NewGuid().ToString();
        return new ServiceCollection()
            .AddDbContext<StatsDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<MatchCompletedConsumer>();
                x.AddConsumer<MatchCreatedConsumer>();
            })
            .BuildServiceProvider(true);
    }

    private static MatchCompleted Completed(Guid winner, Guid loser, bool forfeit = false) => new(
        Guid.NewGuid(),
        winner, "winner", 1020, 20,
        loser, "loser", 980, -20,
        forfeit,
        DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow);

    [Fact]
    public async Task MatchCompleted_UpdatesWinnerAndLoser()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();
        await harness.Bus.Publish(Completed(winner, loser));

        var consumer = harness.GetConsumerHarness<MatchCompletedConsumer>();
        Assert.True(await consumer.Consumed.Any<MatchCompleted>());
        Assert.False(await harness.Published.Any<Fault<MatchCompleted>>());

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StatsDbContext>();
        var w = await db.Players.FindAsync(winner);
        var l = await db.Players.FindAsync(loser);

        Assert.Equal((1, 0, 1020), (w!.Wins, w.Losses, w.Mmr));
        Assert.Equal((0, 1, 980), (l!.Wins, l.Losses, l.Mmr));
        Assert.Equal(1, (await db.Daily.SingleAsync()).MatchesCompleted);
    }

    [Fact]
    public async Task MatchCreated_IsCountedPerDay()
    {
        await using var provider = BuildProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var today = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            await harness.Bus.Publish(new MatchCreated(Guid.NewGuid(),
                Guid.NewGuid(), "a", 1000, Guid.NewGuid(), "b", 1000, today));

        var consumer = harness.GetConsumerHarness<MatchCreatedConsumer>();
        // Harness olayları asenkron işler; üçünün de tüketilmesini bekle
        for (var t = 0; t < 50 && consumer.Consumed.Select<MatchCreated>().Count() < 3; t++)
            await Task.Delay(100);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StatsDbContext>();
        var day = await db.Daily.SingleAsync();
        Assert.Equal(DateOnly.FromDateTime(today), day.Day);
        Assert.Equal(3, day.MatchesCreated);
    }
}
