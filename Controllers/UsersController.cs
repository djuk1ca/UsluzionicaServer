using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Users;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(UserService userService) : ControllerBase
{
    // ── GET /api/users/me ─────────────────────────────────────────────────
    /// <summary>
    /// Vraća profil trenutno prijavljenog korisnika.
    /// Zahteva validan JWT u Authorization headeru.
    /// </summary>
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMe()
    {
        var userId  = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var profile = await userService.GetProfileAsync(userId);
        return Ok(new { success = true, data = profile });
    }

    // ── PUT /api/users/me ─────────────────────────────────────────────────
    /// <summary>
    /// Ažurira ime i grad prijavljenog korisnika.
    /// Grad mora biti iz liste srpskih opština (GET /api/locations/cities).
    /// </summary>
    [Authorize]
    [HttpPut("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await userService.UpdateProfileAsync(userId, dto);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Profil ažuriran." });
    }

    // ── POST /api/users/me/avatar ─────────────────────────────────────────
    /// <summary>
    /// Upload profilne slike. Dozvoljeni formati: JPEG, PNG, WebP. Max 5 MB.
    /// Šalje se kao multipart/form-data sa poljem "file".
    /// </summary>
    [Authorize]
    [HttpPost("me/avatar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadAvatar(IFormFile file)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (url, error) = await userService.UploadAvatarAsync(userId, file);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = new { avatarUrl = url } });
    }

    // ── GET /api/users/{id} ───────────────────────────────────────────────
    /// <summary>
    /// Javni profil korisnika — vidljiv svima, bez autentifikacije.
    /// Ne vraća TokenBalance ni ReferralCode (privatni podaci).
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PublicUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicProfile(string id)
    {
        var profile = await userService.GetProfileAsync(id);
        if (profile is null)
            return NotFound(new { success = false, message = "Korisnik nije pronađen." });

        // Javnom profilu skrivamo finansijske i referral podatke
        return Ok(new
        {
            success = true,
            data    = new PublicUserDto
            {
                Id              = profile.Id,
                FullName        = profile.FullName,
                ProfileImageUrl = profile.ProfileImageUrl,
                IsProvider      = profile.IsProvider,
                LastKnownCity   = profile.LastKnownCity,
                CreatedAt       = profile.CreatedAt
            }
        });
    }
}

/// <summary>Podskup podataka koji se prikazuje na javnom profilu.</summary>
public sealed class PublicUserDto
{
    public string   Id              { get; set; } = string.Empty;
    public string   FullName        { get; set; } = string.Empty;
    public string?  ProfileImageUrl { get; set; }
    public bool     IsProvider      { get; set; }
    public string?  LastKnownCity   { get; set; }
    public DateTime CreatedAt       { get; set; }
}
