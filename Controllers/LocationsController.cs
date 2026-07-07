using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Infrastructure;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/locations")]
public sealed class LocationsController : ControllerBase
{
    // ── GET /api/locations/cities ─────────────────────────────────────────
    /// <summary>
    /// Vraća listu svih opština u Srbiji.
    /// Klijent koristi ovu listu za autocomplete/dropdown pri odabiru lokacije.
    /// Opciono filtriranje sa ?q=novi (case-insensitive pretraga).
    /// </summary>
    [HttpGet("cities")]
    public IActionResult GetCities([FromQuery] string? q)
    {
        var result = string.IsNullOrWhiteSpace(q)
            ? SerbianMunicipalities.All
            : SerbianMunicipalities.All
                .Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return Ok(new { success = true, data = result, total = result.Count });
    }
}
