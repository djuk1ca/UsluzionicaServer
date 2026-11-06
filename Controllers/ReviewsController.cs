using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api")]
public sealed class ReviewsController(ReviewService reviewService) : ControllerBase
{
    // ── POST /api/reviews ─────────────────────────────────────────────────
    /// <summary>
    /// Strana u izvršenoj usluzi (BookingRequestId) ili poslu (JobId) ocenjuje
    /// drugu stranu. Ocena je skrivena dok i druga strana ne oceni ili ne
    /// prođe rok od 14 dana.
    /// </summary>
    [Authorize]
    [HttpPost("reviews")]
    [ProducesResponseType(typeof(ReviewDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateReviewDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (review, error) = await reviewService.CreateAsync(userId, dto);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return StatusCode(StatusCodes.Status201Created,
            new { success = true, data = review });
    }

    // ── GET /api/reviews/pending ──────────────────────────────────────────
    /// <summary>
    /// Izvršene usluge i poslovi u roku koje korisnik još nije ocenio — obe
    /// uloge (klijent i uslugodavac, autor i izvršilac).
    /// </summary>
    [Authorize]
    [HttpGet("reviews/pending")]
    public async Task<IActionResult> Pending()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var list   = await reviewService.PendingAsync(userId);
        return Ok(new { success = true, data = list, total = list.Count });
    }

    // ── GET /api/users/{id}/client-reviews ────────────────────────────────
    /// <summary>
    /// Ocene korisnika kao naručioca, sa prosekom. Traži prijavu — korisnik
    /// bez naloga uslugodavca nema javni profil.
    /// </summary>
    [Authorize]
    [HttpGet("users/{id}/client-reviews")]
    public async Task<IActionResult> GetClientReviews(
        string id,
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20)
    {
        var dto = await reviewService.GetClientReviewsAsync(
            id, page, pageSize, User.FindFirstValue(ClaimTypes.NameIdentifier));

        return dto is null
            ? NotFound(new { success = false, message = "Korisnik nije pronađen." })
            : Ok(new { success = true, data = dto });
    }

    // ── GET /api/listings/{id}/reviews ────────────────────────────────────
    /// <summary>
    /// Sve recenzije jednog oglasa, sortirane od najnovije.
    /// Javni endpoint. Query: ?page=1&amp;pageSize=20
    /// </summary>
    [HttpGet("listings/{id:int}/reviews")]
    [ProducesResponseType(typeof(List<ReviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByListing(
        int id,
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20)
    {
        var reviews = await reviewService.GetByListingAsync(id, page, pageSize, User.FindFirstValue(ClaimTypes.NameIdentifier));
        return Ok(new { success = true, data = reviews, total = reviews.Count });
    }

    // ── GET /api/provider/{id}/reviews ────────────────────────────────────
    /// <summary>
    /// Sve recenzije svih oglasa jednog providera (providerProfileId), sortirane od najnovije.
    /// Javni endpoint. Query: ?page=1&amp;pageSize=20
    /// </summary>
    [HttpGet("provider/{id:int}/reviews")]
    [ProducesResponseType(typeof(List<ReviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByProvider(
        int id,
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20)
    {
        var reviews = await reviewService.GetByProviderAsync(id, page, pageSize, User.FindFirstValue(ClaimTypes.NameIdentifier));
        return Ok(new { success = true, data = reviews, total = reviews.Count });
    }

    // ── GET /api/provider/{id}/reviews/summary ────────────────────────────
    /// <summary>
    /// Agregirane statistike recenzija providera:
    /// prosečna ocena, ukupan broj, raspored po zvezdicama (1–5).
    /// Javni endpoint.
    /// </summary>
    [HttpGet("provider/{id:int}/reviews/summary")]
    [ProducesResponseType(typeof(ReviewSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSummary(int id)
    {
        var summary = await reviewService.GetSummaryAsync(id);

        if (summary is null)
            return NotFound(new { success = false, message = "Provider nije pronađen." });

        return Ok(new { success = true, data = summary });
    }
}
