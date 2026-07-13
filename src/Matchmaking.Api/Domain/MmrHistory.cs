namespace Matchmaking.Api.Domain;

public class MmrHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlayerId { get; set; }
    public Guid MatchId { get; set; }
    public int OldMmr { get; set; }
    public int NewMmr { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
