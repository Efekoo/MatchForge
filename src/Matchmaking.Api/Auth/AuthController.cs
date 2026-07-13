using Matchmaking.Api.Data;
using Matchmaking.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Matchmaking.Api.Auth;

[ApiController]
[Route("auth")]
public class AuthController(AppDbContext db, JwtService jwt) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Length is < 3 or > 32)
            return BadRequest(new { error = "Username must be 3-32 characters." });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return BadRequest(new { error = "Password must be at least 8 characters." });
        if (string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains('@'))
            return BadRequest(new { error = "Invalid email." });

        var username = req.Username.Trim();
        var email = req.Email.Trim().ToLowerInvariant();

        if (await db.Players.AnyAsync(p => p.Username == username || p.Email == email))
            return Conflict(new { error = "Username or email already taken." });

        var player = new Player
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
        };
        db.Players.Add(player);
        await db.SaveChangesAsync();

        return Ok(await IssueTokens(player));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var player = await db.Players.FirstOrDefaultAsync(p => p.Username == req.Username.Trim());
        if (player is null || !BCrypt.Net.BCrypt.Verify(req.Password, player.PasswordHash))
            return Unauthorized(new { error = "Invalid username or password." });

        return Ok(await IssueTokens(player));
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest req)
    {
        var hash = JwtService.HashToken(req.RefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (stored is null || stored.Revoked || stored.ExpiresAt < DateTime.UtcNow)
            return Unauthorized(new { error = "Invalid or expired refresh token." });

        var player = await db.Players.FindAsync(stored.PlayerId);
        if (player is null) return Unauthorized();

        // Rotasyon: eski token iptal edilir, yenisi verilir.
        stored.Revoked = true;
        return Ok(await IssueTokens(player));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest req)
    {
        var hash = JwtService.HashToken(req.RefreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
        if (stored is not null)
        {
            stored.Revoked = true;
            await db.SaveChangesAsync();
        }
        return NoContent();
    }

    private async Task<AuthResponse> IssueTokens(Player player)
    {
        var refreshToken = JwtService.CreateRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            PlayerId = player.Id,
            TokenHash = JwtService.HashToken(refreshToken),
            ExpiresAt = DateTime.UtcNow.Add(JwtService.RefreshTokenLifetime)
        });
        await db.SaveChangesAsync();

        var accessToken = jwt.CreateAccessToken(player.Id, player.Username);
        return new AuthResponse(accessToken, refreshToken, player.Id, player.Username, player.Mmr);
    }
}
