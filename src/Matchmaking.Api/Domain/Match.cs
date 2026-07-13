namespace Matchmaking.Api.Domain;

public class Match
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlayerAId { get; set; }
    public Guid PlayerBId { get; set; }
    public Guid? WinnerId { get; set; }
    public int MmrDeltaA { get; set; }
    public int MmrDeltaB { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
}
