using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Matchmaking.Stats.Data;

/// <summary>Oyuncu başına kazanma/kaybetme ve seri bilgisi: liderlik tablosunun okuma modeli.</summary>
public class PlayerStats
{
    public Guid PlayerId { get; set; }
    public string Username { get; set; } = "";
    public int Mmr { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int ForfeitLosses { get; set; }
    public int CurrentStreak { get; set; }
    public int BestStreak { get; set; }
    public DateTime LastMatchAt { get; set; }
}

/// <summary>Gün bazında açılan ve tamamlanan maç sayısı.</summary>
public class DailyActivity
{
    public DateOnly Day { get; set; }
    public int MatchesCreated { get; set; }
    public int MatchesCompleted { get; set; }
}

/// <summary>
/// Stats servisinin kendi veritabanı. Api'nin tablolarına hiç dokunmaz:
/// servisler arasındaki tek bağ RabbitMQ üzerindeki olaylardır.
/// </summary>
public class StatsDbContext(DbContextOptions<StatsDbContext> options) : DbContext(options)
{
    public DbSet<PlayerStats> Players => Set<PlayerStats>();
    public DbSet<DailyActivity> Daily => Set<DailyActivity>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PlayerStats>(e =>
        {
            e.ToTable("player_stats");
            e.HasKey(p => p.PlayerId);
            e.Property(p => p.Username).HasMaxLength(32);
            e.HasIndex(p => p.Wins);
        });

        mb.Entity<DailyActivity>(e =>
        {
            e.ToTable("daily_activity");
            e.HasKey(d => d.Day);
        });

        // Inbox: aynı mesaj iki kez gelirse (at-least-once teslimat) ikinci kez işlenmez.
        // Consumer'ın DB değişiklikleri ve inbox kaydı aynı transaction'da yazılır.
        mb.AddInboxStateEntity();
        mb.AddOutboxMessageEntity();
        mb.AddOutboxStateEntity();
    }
}
