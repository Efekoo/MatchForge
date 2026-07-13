using System.Security.Claims;
using Matchmaking.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Matchmaking.Api.Players;

[ApiController]
[Route("players")]
[Authorize]
public class PlayersController(AppDbContext db) : ControllerBase
{
    private Guid PlayerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var p = await db.Players.FindAsync(PlayerId);
        if (p is null) return NotFound();
        return Ok(new { p.Id, p.Username, p.Mmr, p.GamesPlayed, p.CreatedAt });
    }

    [HttpGet("me/history")]
    public async Task<IActionResult> History()
    {
        var id = PlayerId;
        var history = await db.MmrHistory
            .Where(h => h.PlayerId == id)
            .OrderByDescending(h => h.CreatedAt)
            .Take(50)
            .Select(h => new { h.MatchId, h.OldMmr, h.NewMmr, h.CreatedAt })
            .ToListAsync();
        return Ok(history);
    }
}
