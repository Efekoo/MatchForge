using System.Net.Http.Json;

namespace Matchmaking.Tests.Integration;

public record AuthResult(string AccessToken, string RefreshToken, Guid PlayerId, string Username, int Mmr);
public record ProfileResult(Guid Id, string Username, int Mmr, int GamesPlayed);

public static class TestClientExtensions
{
    /// <summary>Benzersiz kullanıcı adıyla kayıt olur, token döner.</summary>
    public static async Task<AuthResult> RegisterAsync(this HttpClient client, string prefix = "player")
    {
        var username = $"{prefix}_{Guid.NewGuid():N}"[..24];
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            username,
            email = $"{username}@test.local",
            password = "password123"
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResult>())!;
    }

    public static HttpClient WithToken(this HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        return client;
    }

    public static async Task<ProfileResult> GetProfileAsync(this HttpClient client)
        => (await client.GetFromJsonAsync<ProfileResult>("/players/me"))!;
}
