using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Matchmaking.Tests.Integration;

[Collection("api")]
public class AuthAndQueueTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_Then_Profile_ReturnsInitialMmr()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync();

        var profile = await client.WithToken(auth.AccessToken).GetProfileAsync();

        Assert.Equal(auth.Username, profile.Username);
        Assert.Equal(1000, profile.Mmr);
        Assert.Equal(0, profile.GamesPlayed);
    }

    [Fact]
    public async Task Refresh_RotatesToken_And_OldTokenIsRevoked()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync();

        var refreshed = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // Rotasyon: aynı refresh token ikinci kez kullanılamaz
        var reused = await client.PostAsJsonAsync("/auth/refresh",
            new { refreshToken = auth.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/players/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ParallelQueueJoins_OnlyOneSucceeds()
    {
        // Race condition testi: aynı oyuncu iki cihazdan aynı anda kuyruğa girerse
        // ZADD NX atomikliği sayesinde yalnızca bir istek kabul edilmelidir.
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync("race");
        client.WithToken(auth.AccessToken);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.PostAsync("/queue/join", null)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        // Temizlik: kuyruğu terk et (diğer testleri etkilemesin)
        await client.DeleteAsync("/queue/leave");
    }

    [Fact]
    public async Task QueueStatus_ReflectsJoinAndLeave()
    {
        var client = factory.CreateClient();
        var auth = await client.RegisterAsync("status");
        client.WithToken(auth.AccessToken);

        var joined = await client.PostAsync("/queue/join", null);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        var status = await client.GetFromJsonAsync<QueueStatusResult>("/queue/status");
        Assert.True(status!.InQueue);

        await client.DeleteAsync("/queue/leave");
        status = await client.GetFromJsonAsync<QueueStatusResult>("/queue/status");
        Assert.False(status!.InQueue);
    }

    private record QueueStatusResult(bool InQueue, double WaitSeconds, int CurrentWindow, long QueueLength);
}
