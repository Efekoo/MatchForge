using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace Matchmaking.Tests.Integration;

/// <summary>
/// Testcontainers ile gerçek Postgres + Redis ayağa kaldırır ve API'yi
/// in-memory TestServer üzerinde bu container'lara bağlar.
/// Matchmaker ve LobbyReaper BackgroundService'leri de gerçekten çalışır —
/// yani eşleştirme testleri gerçek akışı test eder, mock yoktur.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("matchmaking")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _redis.StartAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("Jwt:Key", "integration-test-key-0123456789abcdef0123456789abcdef");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }
}

/// <summary>Tüm integration testler aynı container çiftini paylaşır (hız için).</summary>
[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
