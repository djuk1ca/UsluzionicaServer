using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Admin;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminController(
    AdminService          adminService,
    ReportService         reportService,
    UserModerationService userModeration) : ControllerBase
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

    /// <summary>
    /// Deaktivira ili vraća nalog. <c>active=false</c> gasi, <c>active=true</c> vraća.
    ///
    /// TRAŽENO STANJE SE ŠALJE EKSPLICITNO, ranije je bio toggle.
    /// Toggle je obrtao ono što je u bazi, a ne ono što admin vidi na ekranu:
    /// dupli klik ili ustajala lista vraćali su nalog u rad bez ikakvog traga.
    /// Kod moderacione odluke to je preskupa greška.
    ///
    /// Deaktivacija povlači i arhiviranje oglasa i poništavanje refresh tokena —
    /// vidi <see cref="UserModerationService"/>.
    /// </summary>
    [HttpPatch("users/{id}/deactivate")]
    public async Task<IActionResult> SetUserActive(string id, [FromQuery] bool active = false)
    {
        var (ok, error) = active
            ? await userModeration.VratiAsync(id)
            : await userModeration.DeaktivirajAsync(id, "Odluka administratora.");

        if (!ok)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true });
    }

    /// <summary>Admin ručno dodeljuje tokene korisniku (kompenzacija, promocija i sl.).</summary>
    [HttpPost("users/{id}/grant-tokens")]
    public async Task<IActionResult> GrantTokens(string id, [FromBody] GrantTokensDto dto)
    {
        var (success, error, newBalance) = await adminService.GrantTokensAsync(id, dto.Amount, dto.Note);
        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = new { tokenBalance = newBalance } });
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

    // ── PRIJAVE ────────────────────────────────────────────────────────────

    /// <summary>
    /// Red za pregled — prijavljene mete, poređane po broju prijava.
    ///
    /// Vraća METE, ne pojedinačne prijave: deset prijava istog oglasa je jedan
    /// posao i jedna odluka.
    /// </summary>
    [HttpGet("reports")]
    public async Task<IActionResult> GetReports(
        [FromQuery] ReportStatus status = ReportStatus.Pending)
    {
        var red = await reportService.RedAsync(status);
        return Ok(new { success = true, data = red });
    }

    /// <summary>Pojedinačne prijave za jednu metu, sa napomenama prijavilaca.</summary>
    [HttpGet("reports/details")]
    public async Task<IActionResult> GetReportDetails(
        [FromQuery] ReportTargetType targetType,
        [FromQuery] int?             listingId      = null,
        [FromQuery] string?          reportedUserId = null)
    {
        var detalji = await reportService.DetaljiAsync(targetType, listingId, reportedUserId);
        return Ok(new { success = true, data = detalji });
    }

    /// <summary>Broj nerešenih prijava — značka na tabu.</summary>
    [HttpGet("reports/count")]
    public async Task<IActionResult> GetPendingReportCount()
    {
        var broj = await reportService.BrojNeresenihAsync();
        return Ok(new { success = true, data = broj });
    }

    /// <summary>
    /// Rešava SVE nerešene prijave za jednu metu jednom odlukom.
    /// Radnje: ukloni oglas, deaktiviraj nalog, odbij prijavu.
    /// </summary>
    [HttpPost("reports/resolve")]
    public async Task<IActionResult> ResolveReport([FromBody] ResolveReportDto dto)
    {
        var adminId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (ok, error) = await reportService.ResiAsync(adminId, dto);

        if (!ok)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true });
    }
}
