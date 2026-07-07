using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/referrals")]
[Authorize]
public sealed class ReferralsController(TokenWalletService walletService) : ControllerBase
{
    // ── GET /api/referrals/my-code ────────────────────────────────────────
    /// <summary>
    /// Vraća sopstveni referral kod i shareable link za deljenje.
    /// Link: {baseUrl}/register?ref={code}
    /// </summary>
    [HttpGet("my-code")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyCode()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var dto    = await walletService.GetMyCodeAsync(userId);

        if (dto is null)
            return NotFound(new { success = false, message = "Korisnik nema referral kod." });

        return Ok(new { success = true, data = dto });
    }

    // ── GET /api/referrals/stats ──────────────────────────────────────────
    /// <summary>
    /// Statistika referral programa:
    /// ukupno pozvano, koliko je postalo provajder, ukupna zarada u tokenima,
    /// detaljna lista pozvanika sa statusima.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var stats  = await walletService.GetReferralStatsAsync(userId);

        return Ok(new { success = true, data = stats });
    }
}
