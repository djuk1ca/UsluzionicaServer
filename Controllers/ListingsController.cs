using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.DTOs.Tokens;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/listings")]
public sealed class ListingsController(
    ListingService listingService,
    BoostService   boostService) : ControllerBase
{
    // ── GET /api/listings ─────────────────────────────────────────────────
    /// <summary>
    /// Pretražuje aktivne listinge sa paginacijom.
    /// Javni endpoint.
    /// Query: ?q=text &amp;categorySlug=beauty &amp;city=Beograd &amp;page=1 &amp;pageSize=20
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ListingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] ListingQueryParams p)
    {
        var result = await listingService.SearchAsync(p);
        return Ok(new { success = true, data = result });
    }

    // ── GET /api/listings/{id} ────────────────────────────────────────────
    /// <summary>
    /// Vraća detalje jednog listinga i uvećava ViewCount.
    /// Javni endpoint.
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ListingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id)
    {
        // Ako je prosleđen token, identifikuj gledaoca da ne brojimo vlastite preglede
        var viewerId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var listing = await listingService.GetByIdAsync(id, viewerId);
        if (listing is null)
            return NotFound(new { success = false, message = "Listing nije pronađen." });

        return Ok(new { success = true, data = listing });
    }

    // ── GET /api/listings/my ──────────────────────────────────────────────
    /// <summary>
    /// Vraća sve listinge prijavljenog provajdera (osim arhiviranih).
    /// Zahteva autentifikaciju.
    /// </summary>
    [Authorize]
    [HttpGet("my")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMy()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var items  = await listingService.GetByProviderAsync(userId);
        return Ok(new { success = true, data = items, total = items.Count });
    }

    // ── POST /api/listings ────────────────────────────────────────────────
    /// <summary>
    /// Kreira novi listing. Korisnik mora imati ProviderProfile.
    /// </summary>
    [Authorize]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateListingDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (result, error) = await listingService.CreateAsync(userId, dto);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return CreatedAtAction(
            nameof(GetById),
            new { id = result!.Id },
            new { success = true, data = result });
    }

    // ── PUT /api/listings/{id} ────────────────────────────────────────────
    /// <summary>
    /// Ažurira listing. Samo vlasnik.
    /// </summary>
    [Authorize]
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateListingDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await listingService.UpdateAsync(id, userId, dto);

        if (!success && error!.Contains("nije pronađen"))
            return NotFound(new { success = false, message = error });

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Listing ažuriran." });
    }

    // ── PATCH /api/listings/{id}/status ──────────────────────────────────
    /// <summary>
    /// Menja status listinga: Active / Paused / Archived. Samo vlasnik.
    /// Body: { "status": "Paused" }
    /// </summary>
    [Authorize]
    [HttpPatch("{id:int}/status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateListingStatusDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await listingService.UpdateStatusAsync(id, userId, dto.Status);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = $"Status promenjen na '{dto.Status}'." });
    }

    // ── DELETE /api/listings/{id} ─────────────────────────────────────────
    /// <summary>
    /// Arhivira listing (soft delete). Samo vlasnik.
    /// </summary>
    [Authorize]
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await listingService.DeleteAsync(id, userId);

        if (!success)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, message = "Listing arhiviran." });
    }

    // ── POST /api/listings/{id}/images ────────────────────────────────────
    /// <summary>
    /// Dodaje sliku na listing (multipart/form-data, polje "file").
    /// Max 5 slika po listingu, max 10 MB, JPEG/PNG/WebP.
    /// </summary>
    [Authorize]
    [HttpPost("{id:int}/images")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadImage(int id, IFormFile file)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (result, error) = await listingService.UploadImageAsync(id, userId, file);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return StatusCode(StatusCodes.Status201Created,
            new { success = true, data = result });
    }

    // ── DELETE /api/listings/{id}/images/{imageId} ────────────────────────
    /// <summary>
    /// Briše jednu sliku sa listinga. Samo vlasnik.
    /// </summary>
    [Authorize]
    [HttpDelete("{id:int}/images/{imageId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteImage(int id, int imageId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await listingService.DeleteImageAsync(id, imageId, userId);

        if (!success)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, message = "Slika obrisana." });
    }

    // ── POST /api/listings/{id}/boost ─────────────────────────────────────
    /// <summary>
    /// Provider boostuje sopstveni listing trošeći tokene.
    ///
    /// BoostScore = tokensToSpend / durationDays (aditivan — slaže se sa postojećim boostovima).
    /// Dozvoljeni durationDays: 3, 7 ili 14.
    ///
    /// Primer: 6 tokena × 3 dana → BoostScore += 2.0
    ///         7 tokena × 7 dana → BoostScore += 1.0
    ///
    /// BoostExpiryService gasi boost i oduzima BoostScore svakih sat vremena.
    /// </summary>
    [Authorize]
    [HttpPost("{id:int}/boost")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Boost(int id, [FromBody] BoostListingDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await boostService.BoostListingAsync(id, userId, dto);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new
        {
            success = true,
            message = $"Listing boosted za {dto.DurationDays} dana. " +
                      $"BoostScore +{dto.TokensToSpend / dto.DurationDays:0.####}."
        });
    }
}
