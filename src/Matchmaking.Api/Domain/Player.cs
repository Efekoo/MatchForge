namespace Matchmaking.Api.Domain;

public class Player
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public int Mmr { get; set; } = 1000;
    public int GamesPlayed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
