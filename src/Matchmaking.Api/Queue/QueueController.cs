using System.Security.Claims;
using Matchmaking.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Matchmaking.Api.Queue;

[ApiController]
[Route("queue")]
[Authorize]
public class QueueController(QueueService queue, AppDbContext db) : ControllerBase
{
    private Guid PlayerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("join")]
    public async Task<IActionResult> Join()
    {
        var player = await db.Players.FindAsync(PlayerId);
        if (player is null) return Unauthorized();

        var result = await queue.JoinAsync(player.Id, player.Mmr);
        return result switch
        {
            QueueService.JoinResult.Joined => Ok(new { status = "queued", mmr = player.Mmr }),
            QueueService.JoinResult.AlreadyQueued => Conflict(new { error = "Already in queue." }),
            QueueService.JoinResult.AlreadyInMatch => Conflict(new { error = "Already in an active match." }),
            _ => StatusCode(500)
        };
    }

    [HttpDelete("leave")]
    public async Task<IActionResult> Leave()
    {
        var removed = await queue.LeaveAsync(PlayerId);
        return removed ? Ok(new { status = "left" }) : NotFound(new { error = "Not in queue." });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status() => Ok(await queue.GetStatusAsync(PlayerId));
}
