using Matchmaking.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Matchmaking.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MmrHistory> MmrHistory => Set<MmrHistory>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Player>(e =>
        {
            e.ToTable("players");
            e.HasIndex(p => p.Username).IsUnique();
            e.HasIndex(p => p.Email).IsUnique();
            e.Property(p => p.Username).HasMaxLength(32);
            e.Property(p => p.Email).HasMaxLength(256);
        });

        mb.Entity<Match>(e =>
        {
            e.ToTable("matches");
            e.HasIndex(m => m.PlayerAId);
            e.HasIndex(m => m.PlayerBId);
        });

        mb.Entity<MmrHistory>(e =>
        {
            e.ToTable("mmr_history");
            e.HasIndex(h => h.PlayerId);
        });

        mb.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasIndex(t => t.TokenHash);
            e.HasIndex(t => t.PlayerId);
        });
    }
}
