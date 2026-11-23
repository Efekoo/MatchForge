using Matchmaking.Stats.Consumers;
using Matchmaking.Stats.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var pgConn = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=matchmaking_stats;Username=postgres;Password=postgres";
var rabbitConn = builder.Configuration.GetConnectionString("RabbitMq")
    ?? "amqp://guest:guest@localhost:5672";

builder.Services.AddDbContext<StatsDbContext>(o => o.UseNpgsql(pgConn));

builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter(); // kuyruklar: match-completed, match-created

    x.AddConsumer<MatchCompletedConsumer>();
    x.AddConsumer<MatchCreatedConsumer>();

    // Inbox: tüketilen mesajın kimliği StatsDbContext'e yazılır → idempotent consumer
    x.AddEntityFrameworkOutbox<StatsDbContext>(o =>
    {
        o.UsePostgres();
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
    });

    x.AddConfigureEndpointsCallback((context, _, cfg) =>
    {
        // Seriler olay sırasına bağlı: her kuyruk tek tek ve sırayla işlenir.
        // Ölçek gerekirse oyuncu kimliğine göre bölümlenmiş kuyruklara geçilebilir.
        cfg.ConcurrentMessageLimit = 1;
        if (cfg is IRabbitMqReceiveEndpointConfigurator rmq) rmq.PrefetchCount = 1;

        // Geçici hatalar (DB kısa süre erişilemez vb.) için artan aralıklarla yeniden dene.
        // Denemeler tükenince mesaj otomatik olarak <kuyruk>_error'a (dead-letter) taşınır.
        cfg.UseMessageRetry(r => r.Exponential(5,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500)));
        cfg.UseEntityFrameworkOutbox<StatsDbContext>(context);
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(new Uri(rabbitConn));
        cfg.ConfigureEndpoints(context);
    });
});

// Host, bus RabbitMQ'ya bağlanıp kuyruk/exchange bağlarını kurmadan "hazır" sayılmasın:
// aksi halde ilk açılışta, kuyruk henüz bağlanmamışken yayınlanan olaylar kaybolabilir.
builder.Services.Configure<MassTransitHostOptions>(o =>
{
    o.WaitUntilStarted = true;
    o.StartTimeout = TimeSpan.FromSeconds(30);
    o.StopTimeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Şema oluşturma: tek replika çalıştığı için kilide gerek yok; Postgres hazır olana kadar dene
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<StatsDbContext>();
    var retries = 10;
    while (true)
    {
        try { db.Database.EnsureCreated(); break; }
        catch when (retries-- > 0) { await Task.Delay(2000); }
    }
}

// --- Okuma uçları ---

app.MapGet("/leaderboard", async (StatsDbContext db, int? top) =>
{
    var take = Math.Clamp(top ?? 10, 1, 100);
    var rows = await db.Players
        .OrderByDescending(p => p.Mmr)
        .ThenByDescending(p => p.Wins)
        .Take(take)
        .Select(p => new
        {
            p.PlayerId, p.Username, p.Mmr, p.Wins, p.Losses,
            winRate = p.Wins + p.Losses == 0 ? 0 : Math.Round(100.0 * p.Wins / (p.Wins + p.Losses), 1),
            p.CurrentStreak, p.BestStreak
        })
        .ToListAsync();
    return Results.Ok(rows);
});

app.MapGet("/stats/players/{id:guid}", async (Guid id, StatsDbContext db) =>
    await db.Players.FindAsync(id) is { } p ? Results.Ok(p) : Results.NotFound());

app.MapGet("/stats/daily", async (StatsDbContext db, int? days) =>
{
    var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-(days ?? 7)));
    return Results.Ok(await db.Daily.Where(d => d.Day >= since).OrderBy(d => d.Day).ToListAsync());
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Integration testlerin WebApplicationFactory ile erişebilmesi için
namespace Matchmaking.Stats
{
    public partial class StatsProgram;
}
