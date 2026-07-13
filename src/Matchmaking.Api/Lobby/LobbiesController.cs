using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Matchmaking.Api.Lobby;

[ApiController]
[Route("lobbies")]
[Authorize]
public class LobbiesController(LobbyStore lobbies) : ControllerBase
{
    private Guid PlayerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Durum sorgulama; canlı olaylar SignalR'dadır.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var lobby = await lobbies.GetAsync(id);
        if (lobby is null || !lobby.Contains(PlayerId)) return NotFound();

        return Ok(new
        {
            lobby.LobbyId,
            playerA = lobby.NameA,
            playerB = lobby.NameB,
            lobby.ScoreA,
            lobby.ScoreB,
            lobby.Round,
            lobby.State
        });
    }

    /// <summary>Reconnect için: oyuncunun aktif lobisi var mı?</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var lobbyId = await lobbies.GetPlayerLobbyIdAsync(PlayerId);
        return lobbyId is null ? NoContent() : Ok(new { lobbyId });
    }
}
