using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace Matchmaking.Tests.Integration;

/// <summary>
/// Uçtan uca akış: kuyruk → eşleşme (gerçek MatchmakerService) → lobi →
/// taş-kağıt-makas (best of 3) → sonuç Postgres'e yazılır → Elo güncellenir.
/// Mock yok; iki gerçek SignalR istemcisi maçı gerçekten oynar.
/// </summary>
[Collection("api")]
public class MatchFlowTests(ApiFactory factory)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TwoPlayers_Queue_Match_PlayBestOfThree_EloUpdates()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var httpA = factory.CreateClient();
        var httpB = factory.CreateClient();
        var authA = await httpA.RegisterAsync("flowa");
        var authB = await httpB.RegisterAsync("flowb");
        httpA.WithToken(authA.AccessToken);
        httpB.WithToken(authB.AccessToken);

        await using var clientA = await ConnectHubAsync(authA.AccessToken, cts.Token);
        await using var clientB = await ConnectHubAsync(authB.AccessToken, cts.Token);

        // İkisi de kuyruğa girer; MMR'lar eşit (1000) → ilk taramada eşleşmeliler
        (await httpA.PostAsync("/queue/join", null, cts.Token)).EnsureSuccessStatusCode();
        (await httpB.PostAsync("/queue/join", null, cts.Token)).EnsureSuccessStatusCode();

        var lobbyA = await clientA.MatchFound.Task.WaitAsync(cts.Token);
        var lobbyB = await clientB.MatchFound.Task.WaitAsync(cts.Token);
        Assert.Equal(lobbyA, lobbyB); // aynı lobiye düştüler

        await clientA.Conn.InvokeAsync("JoinLobby", lobbyA, cts.Token);
        await clientB.Conn.InvokeAsync("JoinLobby", lobbyB, cts.Token);
        await clientA.MatchStarted.Task.WaitAsync(cts.Token);
        await clientB.MatchStarted.Task.WaitAsync(cts.Token);

        // A hep taş, B hep makas oynar → A iki round üst üste kazanır (2-0)
        for (var round = 0; round < 2; round++)
        {
            await clientA.Conn.InvokeAsync("MakeMove", lobbyA, "rock", cts.Token);
            await clientB.Conn.InvokeAsync("MakeMove", lobbyB, "scissors", cts.Token);
            await clientA.RoundResults.Reader.ReadAsync(cts.Token);
            await clientB.RoundResults.Reader.ReadAsync(cts.Token);
        }

        var ended = await clientA.MatchEnded.Task.WaitAsync(cts.Token);
        Assert.Equal(authA.Username, ended.GetProperty("winner").GetString());
        Assert.False(ended.GetProperty("forfeit").GetBoolean());

        // Elo: iki yeni oyuncu (K=40), eşit rating → kazanan +20, kaybeden -20
        var profileA = await httpA.GetProfileAsync();
        var profileB = await httpB.GetProfileAsync();
        Assert.Equal(1020, profileA.Mmr);
        Assert.Equal(980, profileB.Mmr);
        Assert.Equal(1, profileA.GamesPlayed);
        Assert.Equal(1, profileB.GamesPlayed);

        // MMR geçmişi de yazılmış olmalı (denetlenebilirlik)
        var history = await httpA.GetFromJsonAsync<List<HistoryEntry>>("/players/me/history", cts.Token);
        Assert.Single(history!);
    }

    private record HistoryEntry(Guid MatchId, int OldMmr, int NewMmr, DateTime CreatedAt);

    [Fact]
    public async Task InvalidMove_IsRejected_ServerSide()
    {
        using var cts = new CancellationTokenSource(Timeout);

        var httpA = factory.CreateClient();
        var httpB = factory.CreateClient();
        var authA = await httpA.RegisterAsync("cheata");
        var authB = await httpB.RegisterAsync("cheatb");
        httpA.WithToken(authA.AccessToken);
        httpB.WithToken(authB.AccessToken);

        await using var clientA = await ConnectHubAsync(authA.AccessToken, cts.Token);
        await using var clientB = await ConnectHubAsync(authB.AccessToken, cts.Token);

        (await httpA.PostAsync("/queue/join", null, cts.Token)).EnsureSuccessStatusCode();
        (await httpB.PostAsync("/queue/join", null, cts.Token)).EnsureSuccessStatusCode();

        var lobby = await clientA.MatchFound.Task.WaitAsync(cts.Token);
        await clientA.Conn.InvokeAsync("JoinLobby", lobby, cts.Token);
        await clientB.Conn.InvokeAsync("JoinLobby", await clientB.MatchFound.Task, cts.Token);
        await clientA.MatchStarted.Task.WaitAsync(cts.Token);

        // Geçersiz hamle sunucuda reddedilmeli (istemciye güvenilmez)
        await clientA.Conn.InvokeAsync("MakeMove", lobby, "lizard", cts.Token);
        var error = await clientA.Errors.Reader.ReadAsync(cts.Token);
        Assert.Contains("Invalid move", error);

        // Aynı round'da ikinci hamle de reddedilmeli
        await clientA.Conn.InvokeAsync("MakeMove", lobby, "rock", cts.Token);
        await clientA.Conn.InvokeAsync("MakeMove", lobby, "paper", cts.Token);
        error = await clientA.Errors.Reader.ReadAsync(cts.Token);
        Assert.Contains("already moved", error);
    }

    // --- yardımcılar ---

    private async Task<HubClient> ConnectHubAsync(string accessToken, CancellationToken ct)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/game"), options =>
            {
                // TestServer WebSocket desteklemez; LongPolling in-memory handler ile çalışır
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();

        var client = new HubClient(conn);
        conn.On<JsonElement>("MatchFound", e =>
            client.MatchFound.TrySetResult(e.GetProperty("lobbyId").GetString()!));
        conn.On<JsonElement>("MatchStarted", _ => client.MatchStarted.TrySetResult());
        conn.On<JsonElement>("RoundResult", e => client.RoundResults.Writer.TryWrite(e));
        conn.On<JsonElement>("MatchEnded", e => client.MatchEnded.TrySetResult(e));
        conn.On<string>("Error", e => client.Errors.Writer.TryWrite(e));

        await conn.StartAsync(ct);
        return client;
    }

    private sealed class HubClient(HubConnection conn) : IAsyncDisposable
    {
        public HubConnection Conn { get; } = conn;
        public TaskCompletionSource<string> MatchFound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> MatchEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<JsonElement> RoundResults { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<string> Errors { get; } = Channel.CreateUnbounded<string>();

        public ValueTask DisposeAsync() => Conn.DisposeAsync();
    }
}
