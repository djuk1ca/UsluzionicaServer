using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminController(AdminService adminService) : ControllerBase
{
    // ── KORISNICI ──────────────────────────────────────────────────────────

    /// <summary>Lista svih korisnika sa opcionim pretragom.</summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int    page     = 1,
        [FromQuery] int    pageSize = 20,
        [FromQuery] string? search  = null)
    {
        var (items, total) = await adminService.GetUsersAsync(page, pageSize, search);
        return Ok(new { success = true, data = items, total, page, pageSize });
    }

    /// <summary>Toggle IsActive za korisnika (deaktivacija / reaktivacija).</summary>
    [HttpPatch("users/{id}/deactivate")]
    public async Task<IActionResult> DeactivateUser(string id)
    {
        var updated = await adminService.DeactivateUserAsync(id);
        if (!updated)
            return NotFound(new { success = false, message = "Korisnik nije pronađen." });

        return Ok(new { success = true });
    }

    // ── LISTINZI ───────────────────────────────────────────────────────────

    /// <summary>Lista svih listinga, opcionо filterisana po statusu.</summary>
    [HttpGet("listings")]
    public async Task<IActionResult> GetListings(
        [FromQuery] int    page     = 1,
        [FromQuery] int    pageSize = 20,
        [FromQuery] string? status  = null)
    {
        var (items, total) = await adminService.GetListingsAsync(page, pageSize, status);
        return Ok(new { success = true, data = items, total, page, pageSize });
    }

    /// <summary>Moderacija — arhivira listing.</summary>
    [HttpPatch("listings/{id:int}/archive")]
    public async Task<IActionResult> ArchiveListing(int id)
    {
        var updated = await adminService.ArchiveListingAsync(id);
        if (!updated)
            return NotFound(new { success = false, message = "Listing nije pronađen." });

        return Ok(new { success = true });
    }

    // ── PROVAJDERI ─────────────────────────────────────────────────────────

    /// <summary>Dodeljuje verified badge provajderu.</summary>
    [HttpPost("providers/{id:int}/verify")]
    public async Task<IActionResult> VerifyProvider(int id)
    {
        var updated = await adminService.VerifyProviderAsync(id);
        if (!updated)
            return NotFound(new { success = false, message = "Provajder profil nije pronađen." });

        return Ok(new { success = true });
    }

    // ── TOKEN LOG ──────────────────────────────────────────────────────────

    /// <summary>TokenTransaction log svih korisnika, opcionо filterisan po vrsti i korisniku.</summary>
    [HttpGet("tokens")]
    public async Task<IActionResult> GetTokenLog(
        [FromQuery] int    page     = 1,
        [FromQuery] int    pageSize = 30,
        [FromQuery] string? kind    = null,
        [FromQuery] string? userId  = null)
    {
        var (items, total) = await adminService.GetTokenLogAsync(page, pageSize, kind, userId);
        return Ok(new { success = true, data = items, total, page, pageSize });
    }

    // ── STATISTIKE ─────────────────────────────────────────────────────────

    /// <summary>Agregirane statistike za admin dashboard.</summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var stats = await adminService.GetStatsAsync();
        return Ok(new { success = true, data = stats });
    }

    // ── ANALITIKA ──────────────────────────────────────────────────────────

    /// <summary>Dnevni token tokovi za grafikone — zadnjih N dana (default 30).</summary>
    [HttpGet("analytics")]
    public async Task<IActionResult> GetAnalytics([FromQuery] int days = 30)
    {
        var analytics = await adminService.GetTokenAnalyticsAsync(days);
        return Ok(new { success = true, data = analytics });
    }
}
