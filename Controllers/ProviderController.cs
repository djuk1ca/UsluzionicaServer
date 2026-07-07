using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/provider")]
public sealed class ProviderController(ProviderService providerService) : ControllerBase
{
    // ── POST /api/provider/activate ───────────────────────────────────────
    /// <summary>
    /// Aktivira provajderski status za prijavljenog korisnika.
    /// Kreira ProviderProfile, postavlja IsProvider=true, okida referral nagradu.
    /// Zahteva verifikovan email.
    /// </summary>
    [Authorize]
    [HttpPost("activate")]
    [ProducesResponseType(typeof(ProviderProfileDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Activate([FromBody] ActivateProviderDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (result, error) = await providerService.ActivateAsync(userId, dto);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return StatusCode(StatusCodes.Status201Created,
            new { success = true, data = result });
    }

    // ── GET /api/provider/me ──────────────────────────────────────────────
    /// <summary>
    /// Vraća sopstveni provajder profil sa listinzima i kategorijama.
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(ProviderProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMe()
    {
        var userId  = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var profile = await providerService.GetMyProfileAsync(userId);

        if (profile is null)
            return NotFound(new
            {
                success = false,
                message = "Provajder profil nije pronađen. Koristi POST /api/provider/activate."
            });

        return Ok(new { success = true, data = profile });
    }

    // ── PUT /api/provider/me ──────────────────────────────────────────────
    /// <summary>
    /// Ažurira provajder profil (profesija, bio, lokacija, Instagram, kategorije).
    /// Kategorije se potpuno zamenjuju novom listom.
    /// </summary>
    [Authorize]
    [HttpPut("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProviderDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await providerService.UpdateAsync(userId, dto);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Provajder profil ažuriran." });
    }

    // ── POST /api/provider/me/cover ───────────────────────────────────────
    /// <summary>
    /// Upload cover slike provajder profila.
    /// multipart/form-data, polje "file". Max 10 MB, JPEG/PNG/WebP.
    /// </summary>
    [Authorize]
    [HttpPost("me/cover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadCover(IFormFile file)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (url, error) = await providerService.UploadCoverAsync(userId, file);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = new { coverImageUrl = url } });
    }

    // ── GET /api/provider/{id} ────────────────────────────────────────────
    /// <summary>
    /// Javni profil provajdera sa aktivnim listinzima i kategorijama.
    /// Ne zahteva autentifikaciju.
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ProviderProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublic(int id)
    {
        var profile = await providerService.GetPublicProfileAsync(id);

        if (profile is null)
            return NotFound(new { success = false, message = "Provajder nije pronađen." });

        return Ok(new { success = true, data = profile });
    }

    // ── GET /api/provider/{id}/listings ───────────────────────────────────
    /// <summary>
    /// Aktivni listinzi datog provajdera. Javni endpoint.
    /// </summary>
    [HttpGet("{id:int}/listings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetListings(int id)
    {
        var listings = await providerService.GetProviderListingsAsync(id);
        return Ok(new { success = true, data = listings, total = listings.Count });
    }

    // Reviews su premještene u ReviewsController → GET /api/provider/{id}/reviews
}
