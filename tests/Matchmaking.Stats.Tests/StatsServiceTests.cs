using System.Net.Http.Json;
using Matchmaking.Contracts;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Matchmaking.Stats.Tests;

/// <summary>
/// Uçtan uca: gerçek RabbitMQ + Postgres. Olay broker'a yayınlanır, Stats servisi
/// tüketir, sonuç HTTP uçlarından okunur. Inbox sayesinde aynı mesaj iki kez
/// gelse bile istatistik bir kez sayılır (idempotent consumer).
/// </summary>
public class StatsServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("matchmaking_stats")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-alpine")
        .Build();

    private WebApplicationFactory<StatsProgram> _app = null!;
    private IBusControl _publisher = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        _app = new WebApplicationFactory<StatsProgram>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            b.UseSetting("ConnectionStrings:RabbitMq", _rabbit.GetConnectionString());
        });
        _app.CreateClient(); // host'u başlatır: şema + consumer'lar hazır

        // Api'nin yerine geçen yayıncı
        _publisher = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(_rabbit.GetConnectionString())));
        await _publisher.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _publisher.StopAsync();
        await _app.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    private record LeaderboardRow(Guid PlayerId, string Username, int Mmr, int Wins, int Losses,
        double WinRate, int CurrentStreak, int BestStreak);

    [Fact]
    public async Task PublishedMatches_ShowUpOnLeaderboard_AndDuplicatesAreIgnored()
    {
        var http = _app.CreateClient();
        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();

        var first = new MatchCompleted(Guid.NewGuid(),
            winner, "alice", 1020, 20, loser, "bob", 980, -20, false,
            DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow);
        var second = first with
        {
            MatchId = Guid.NewGuid(), WinnerNewMmr = 1039, LoserNewMmr = 961,
            StartedAt = first.EndedAt.AddMinutes(1), EndedAt = first.EndedAt.AddMinutes(3)
        };

        // İlk maç: aynı MessageId ile iki kez gönderilir (yeniden teslim simülasyonu)
        var duplicateId = NewId.NextGuid();
        await _publisher.Publish(first, ctx => ctx.MessageId = duplicateId);
        await _publisher.Publish(first, ctx => ctx.MessageId = duplicateId);
        await _publisher.Publish(second);

        var board = await PollAsync(http, rows =>
            rows.FirstOrDefault(r => r.PlayerId == winner)?.Wins >= 2);

        var alice = board.Single(r => r.PlayerId == winner);
        var bob = board.Single(r => r.PlayerId == loser);

        Assert.Equal(2, alice.Wins);          // 3 mesaj geldi, tekrar eden 1'i sayılmadı
        Assert.Equal(2, alice.CurrentStreak);
        Assert.Equal(1039, alice.Mmr);
        Assert.Equal(100, alice.WinRate);
        Assert.Equal(2, bob.Losses);
        Assert.Equal(-2, bob.CurrentStreak);
        Assert.Equal("alice", board[0].Username); // MMR'a göre sıralı

        // Tekrar eden mesajın geç işlenmediğinden emin olmak için kısa bir süre daha bekle
        await Task.Delay(1500);
        var again = await http.GetFromJsonAsync<List<LeaderboardRow>>("/leaderboard");
        Assert.Equal(2, again!.Single(r => r.PlayerId == winner).Wins);
    }

    private static async Task<List<LeaderboardRow>> PollAsync(HttpClient http, Func<List<LeaderboardRow>, bool> done)
    {
        for (var i = 0; i < 60; i++)
        {
            var rows = await http.GetFromJsonAsync<List<LeaderboardRow>>("/leaderboard");
            if (rows is not null && done(rows)) return rows;
            await Task.Delay(500);
        }
        throw new TimeoutException("Leaderboard did not reach the expected state in time");
    }
}
