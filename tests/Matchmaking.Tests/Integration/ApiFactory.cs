using System.Threading.Channels;
using Matchmaking.Contracts;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace Matchmaking.Tests.Integration;

/// <summary>
/// Testcontainers ile gerçek Postgres + Redis + RabbitMQ ayağa kaldırır ve API'yi
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

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-alpine")
        .Build();

    private IBusControl? _recorderBus;

    /// <summary>API'nin RabbitMQ'ya yayınladığı olaylar (outbox → broker → bu kuyruk).</summary>
    public EventRecorder Events { get; } = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());

        // Ayrı bir "dinleyici" bus: API'nin yayınladığı olayların broker'a gerçekten
        // ulaştığını, Stats servisinin göreceği şekilde doğrular.
        _recorderBus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(_rabbit.GetConnectionString()));
            cfg.ReceiveEndpoint("test-event-recorder", e =>
            {
                e.Handler<MatchCreated>(ctx => Events.Created.Writer.WriteAsync(ctx.Message).AsTask());
                e.Handler<MatchCompleted>(ctx => Events.Completed.Writer.WriteAsync(ctx.Message).AsTask());
            });
        });
        await _recorderBus.StartAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("ConnectionStrings:RabbitMq", _rabbit.GetConnectionString());
        builder.UseSetting("Jwt:Key", "integration-test-key-0123456789abcdef0123456789abcdef");
    }

    public new async Task DisposeAsync()
    {
        if (_recorderBus is not null) await _recorderBus.StopAsync();
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

public class EventRecorder
{
    public Channel<MatchCreated> Created { get; } = Channel.CreateUnbounded<MatchCreated>();
    public Channel<MatchCompleted> Completed { get; } = Channel.CreateUnbounded<MatchCompleted>();

    /// <summary>Koşulu sağlayan ilk olayı bekler; aynı collection'daki diğer testlerin olaylarını atlar.</summary>
    public static async Task<T> WaitForAsync<T>(Channel<T> channel, Func<T, bool> match, CancellationToken ct)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            if (match(item)) return item;
        throw new OperationCanceledException();
    }
}

/// <summary>Tüm integration testler aynı container setini paylaşır (hız için).</summary>
[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;
