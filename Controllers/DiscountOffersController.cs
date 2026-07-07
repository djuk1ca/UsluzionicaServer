using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.DiscountOffers;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/discount-offers")]
[Authorize]
public sealed class DiscountOffersController(TokenWalletService walletService) : ControllerBase
{
    // ── POST /api/discount-offers ─────────────────────────────────────────
    /// <summary>
    /// Klijent šalje token ponudu provideru za određeni listing.
    /// Tokeni se ne oduzimaju odmah — tek pri prihvatanju.
    /// Body: { receiverId, listingId, tokenAmount, conversationId? }
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(DiscountOfferDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateDiscountOfferDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (offer, error) = await walletService.CreateOfferAsync(userId, dto);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return StatusCode(StatusCodes.Status201Created,
            new { success = true, data = offer });
    }

    // ── GET /api/discount-offers/incoming ─────────────────────────────────
    /// <summary>Ponude koje je korisnik primio (kao provider). Najnovije prvo.</summary>
    [HttpGet("incoming")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Incoming()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var items  = await walletService.GetIncomingOffersAsync(userId);
        return Ok(new { success = true, data = items, total = items.Count });
    }

    // ── GET /api/discount-offers/outgoing ─────────────────────────────────
    /// <summary>Ponude koje je korisnik poslao (kao klijent). Najnovije prvo.</summary>
    [HttpGet("outgoing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Outgoing()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var items  = await walletService.GetOutgoingOffersAsync(userId);
        return Ok(new { success = true, data = items, total = items.Count });
    }

    // ── PATCH /api/discount-offers/{id}/accept ────────────────────────────
    /// <summary>
    /// Provider prihvata ponudu.
    /// Vrši token transfer i kreira 2 TokenTransaction zapisa.
    /// </summary>
    [HttpPatch("{id:int}/accept")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Accept(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await walletService.AcceptOfferAsync(id, userId);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Ponuda prihvaćena. Tokeni su transferovani." });
    }

    // ── PATCH /api/discount-offers/{id}/reject ────────────────────────────
    /// <summary>Provider odbija ponudu. Nema token transakcija.</summary>
    [HttpPatch("{id:int}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reject(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await walletService.RejectOfferAsync(id, userId);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Ponuda odbijena." });
    }
}
