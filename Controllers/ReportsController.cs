using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Prijavljivanje sadržaja. Admin deo stoji u <see cref="AdminController"/>.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController(ReportService reportService) : ControllerBase
{
    // ── POST /api/reports ─────────────────────────────────────────────────
    /// <summary>
    /// Prijavljuje oglas ili korisnika.
    ///
    /// Rate limit „reports": sistem prijava je i sam vektor napada — bez
    /// ograničenja jedan nalog može zasuti red lažnim prijavama konkurencije i
    /// zakloniti stvarne prekršaje.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("reports")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateReportDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (ok, error) = await reportService.PrijaviAsync(userId, dto);

        if (!ok)
            return BadRequest(new { success = false, message = error });

        return Ok(new
        {
            success = true,
            message = "Prijava je primljena. Pregledaćemo je u najkraćem roku."
        });
    }
}
