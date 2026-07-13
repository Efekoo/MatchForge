using System.IdentityModel.Tokens.Jwt;
using Matchmaking.Api.Auth;
using Matchmaking.Api.Data;
using Matchmaking.Api.Lobby;
using Matchmaking.Api.Queue;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// "sub" claim'inin NameIdentifier'a otomatik map'lenmesini bozan legacy davranışı kapat
JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

var pgConn = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=matchmaking;Username=postgres;Password=postgres";
var redisConn = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? "dev-only-change-me-in-prod-0123456789abcdef0123456789abcdef";
builder.Configuration["Jwt:Key"] = jwtKey;

// --- Servisler ---
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(pgConn));
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<LobbyStore>();
builder.Services.AddSingleton<MatchFinalizer>();
builder.Services.AddHostedService<MatchmakerService>();

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- Auth ---
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = JwtService.ValidationParameters(jwtKey);

        // SignalR: WebSocket'te Authorization header taşınamaz; token query string'den gelir
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) &&
                    path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// --- Şema oluşturma (MVP: EnsureCreated; v1.1'de EF migrations'a geçilecek) ---
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var retries = 10;
    while (true)
    {
        try
        {
            db.Database.EnsureCreated();
            break;
        }
        catch when (retries-- > 0)
        {
            await Task.Delay(2000);
        }
    }
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<GameHub>("/hubs/game");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
