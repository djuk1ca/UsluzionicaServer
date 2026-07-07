using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/bookings")]
[Authorize]
public sealed class BookingsController(BookingService bookingService) : ControllerBase
{
    // ── POST /api/bookings ────────────────────────────────────────────────
    /// <summary>
    /// Klijent šalje booking zahtev za listing.
    /// Email mora biti verifikovan. Nema duplikata po listingu.
    /// Body: { "listingId": 1, "notes": "..." }
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateBookingDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (booking, error) = await bookingService.CreateAsync(userId, dto);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return StatusCode(StatusCodes.Status201Created,
            new { success = true, data = booking });
    }

    // ── GET /api/bookings/incoming ────────────────────────────────────────
    /// <summary>
    /// Provider vidi sve zahteve koji su mu poslati.
    /// Sortiranje: najnoviji prvi.
    /// </summary>
    [HttpGet("incoming")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetIncoming()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var list   = await bookingService.GetIncomingAsync(userId);
        return Ok(new { success = true, data = list, total = list.Count });
    }

    // ── GET /api/bookings/outgoing ────────────────────────────────────────
    /// <summary>
    /// Klijent vidi sve zahteve koje je on poslao.
    /// Sortiranje: najnoviji prvi.
    /// </summary>
    [HttpGet("outgoing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOutgoing()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var list   = await bookingService.GetOutgoingAsync(userId);
        return Ok(new { success = true, data = list, total = list.Count });
    }

    // ── PATCH /api/bookings/{id}/confirm ──────────────────────────────────
    /// <summary>
    /// Provider potvrđuje zahtev (Pending → Confirmed).
    /// Startuje 3-dnevni timer (AcceptedAt = now).
    /// </summary>
    [HttpPatch("{id:int}/confirm")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Confirm(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await bookingService.ConfirmAsync(id, userId);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Booking potvrđen." });
    }

    // ── PATCH /api/bookings/{id}/reject ───────────────────────────────────
    /// <summary>Provider odbija zahtev (Pending → Rejected).</summary>
    [HttpPatch("{id:int}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reject(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await bookingService.RejectAsync(id, userId);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Booking odbijen." });
    }

    // ── PATCH /api/bookings/{id}/cancel ───────────────────────────────────
    /// <summary>
    /// Klijent otkazuje zahtev — dostupno samo dok je Pending.
    /// Provider dobija notifikaciju o otkazu.
    /// </summary>
    [HttpPatch("{id:int}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Cancel(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await bookingService.CancelAsync(id, userId);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Booking otkazan." });
    }

    // ── POST /api/bookings/{id}/execute ───────────────────────────────────
    /// <summary>
    /// Provider označava uslugu kao izvršenu (Confirmed → Completed).
    ///
    /// Uslovi:
    ///   - Booking mora biti Confirmed
    ///   - Mora proći 3 dana od AcceptedAt
    ///
    /// Efekti:
    ///   - Kreira ServiceExecution
    ///   - Klijent dobija 0.50 tokena + notifikaciju
    /// </summary>
    [HttpPost("{id:int}/execute")]
    [ProducesResponseType(typeof(BookingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Execute(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (booking, error) = await bookingService.ExecuteAsync(id, userId);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = booking });
    }
}
