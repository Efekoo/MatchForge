using Matchmaking.Contracts;
using Matchmaking.Stats.Data;
using MassTransit;

namespace Matchmaking.Stats.Consumers;

/// <summary>
/// Tamamlanan maçı liderlik tablosuna işler. Hata fırlatırsa MassTransit önce yeniden dener,
/// denemeler tükenince mesajı "match-completed_error" kuyruğuna (dead-letter) taşır.
/// </summary>
public class MatchCompletedConsumer(StatsDbContext db, ILogger<MatchCompletedConsumer> logger)
    : IConsumer<MatchCompleted>
{
    public async Task Consume(ConsumeContext<MatchCompleted> context)
    {
        var m = context.Message;

        var winner = await GetOrCreateAsync(m.WinnerId, m.WinnerName, context.CancellationToken);
        var loser = await GetOrCreateAsync(m.LoserId, m.LoserName, context.CancellationToken);

        ApplyWin(winner, m.WinnerName, m.WinnerNewMmr, m.EndedAt);
        ApplyLoss(loser, m.LoserName, m.LoserNewMmr, m.Forfeit, m.EndedAt);

        var day = await GetOrCreateDayAsync(DateOnly.FromDateTime(m.EndedAt), context.CancellationToken);
        day.MatchesCompleted++;

        // Inbox kaydıyla birlikte tek transaction'da kaydedilir
        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation("Stats updated for match {Match}: {Winner} beat {Loser}{Forfeit}",
            m.MatchId, m.WinnerName, m.LoserName, m.Forfeit ? " (forfeit)" : "");
    }

    public static void ApplyWin(PlayerStats p, string username, int newMmr, DateTime at)
    {
        ApplyLatest(p, username, newMmr, at);
        p.Wins++;
        p.CurrentStreak = p.CurrentStreak >= 0 ? p.CurrentStreak + 1 : 1;
        p.BestStreak = Math.Max(p.BestStreak, p.CurrentStreak);
    }

    public static void ApplyLoss(PlayerStats p, string username, int newMmr, bool forfeit, DateTime at)
    {
        ApplyLatest(p, username, newMmr, at);
        p.Losses++;
        if (forfeit) p.ForfeitLosses++;
        // Negatif seri = üst üste kayıp
        p.CurrentStreak = p.CurrentStreak <= 0 ? p.CurrentStreak - 1 : -1;
    }

    /// <summary>
    /// "Son durum" alanları yalnızca daha yeni bir maçtan güncellenir: geç teslim edilen
    /// eski bir olay, güncel MMR'ın üzerine yazamaz (son yazan kazanır hatasını önler).
    /// </summary>
    private static void ApplyLatest(PlayerStats p, string username, int newMmr, DateTime at)
    {
        if (at < p.LastMatchAt) return;
        p.Username = username;
        p.Mmr = newMmr;
        p.LastMatchAt = at;
    }

    private async Task<PlayerStats> GetOrCreateAsync(Guid id, string username, CancellationToken ct)
    {
        var p = await db.Players.FindAsync([id], ct);
        if (p is not null) return p;

        p = new PlayerStats { PlayerId = id, Username = username };
        db.Players.Add(p);
        return p;
    }

    private async Task<DailyActivity> GetOrCreateDayAsync(DateOnly day, CancellationToken ct)
    {
        var d = await db.Daily.FindAsync([day], ct);
        if (d is not null) return d;

        d = new DailyActivity { Day = day };
        db.Daily.Add(d);
        return d;
    }
}
