using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Blokiranje korisnika.
///
/// Zaseban kontroler, a ne dodatak na <c>UsersController</c>: blokiranje nije
/// operacija nad tuđim nalogom nego nad sopstvenom listom, pa i putanje idu
/// pod <c>/api/blocks</c>. Uz to <c>UsersController</c> već nosi profil,
/// avatar i brisanje naloga.
/// </summary>
[ApiController]
[Route("api/blocks")]
[Authorize]
public sealed class BlocksController(BlockService blockService) : ControllerBase
{
    // ── GET /api/blocks ───────────────────────────────────────────────────
    /// <summary>Koga je prijavljeni korisnik blokirao.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<BlockedUserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBlocked()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var lista  = await blockService.ListaBlokiranihAsync(userId);

        return Ok(new { success = true, data = lista });
    }

    // ── POST /api/blocks/{id} ─────────────────────────────────────────────
    /// <summary>
    /// Blokira korisnika. Idempotentno — ponovljeni poziv vraća 200.
    /// </summary>
    [HttpPost("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Block(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (ok, error) = await blockService.BlokirajAsync(userId, id);

        if (!ok)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Korisnik je blokiran." });
    }

    // ── DELETE /api/blocks/{id} ───────────────────────────────────────────
    /// <summary>Uklanja blokadu koju je postavio prijavljeni korisnik.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unblock(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (ok, error) = await blockService.OdblokirajAsync(userId, id);

        if (!ok)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, message = "Blokada je uklonjena." });
    }
}
