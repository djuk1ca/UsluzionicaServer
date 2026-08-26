using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Search;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/locations")]
public sealed class LocationsController : ControllerBase
{
    /// <summary>
    /// Foldovana imena opština, izračunata jednom.
    ///
    /// Lista je statična (106 opština), pa nema smisla foldovati je pri svakom
    /// pritisku tastera u autocomplete polju.
    /// </summary>
    private static readonly (string Original, string Folded)[] Cities =
        SerbianMunicipalities.All
            .Select(c => (Original: c, Folded: SearchNormalizer.Fold(c)))
            .ToArray();

    // ── GET /api/locations/cities ─────────────────────────────────────────
    /// <summary>
    /// Lista opština u Srbiji za autocomplete pri odabiru lokacije.
    /// Opciono filtriranje sa ?q=.
    ///
    /// Poređenje ide preko foldovanih vrednosti, pa „cacak" nalazi „Čačak",
    /// „sabac" nalazi „Šabac", a „нови сад" nalazi „Novi Sad". Ranije je bilo
    /// `Contains(q, OrdinalIgnoreCase)` — neosetljivo na velika/mala slova, ali
    /// potpuno osetljivo na dijakritiku, pa je korisnik bez srpske tastature
    /// morao pogoditi tačan oblik.
    /// </summary>
    [HttpGet("cities")]
    public IActionResult GetCities([FromQuery] string? q)
    {
        var needle = SearchNormalizer.Fold(q);

        if (needle.Length == 0)
        {
            var all = Cities.Select(c => c.Original).ToList();
            return Ok(new { success = true, data = all, total = all.Count });
        }

        // Opštine koje POČINJU upitom idu prvo — kad korisnik kuca „nov",
        // „Novi Sad" je verovatniji od „Bela Crkva — Banatska Palanka".
        var result = Cities
            .Where(c => c.Folded.Contains(needle, StringComparison.Ordinal))
            .OrderByDescending(c => c.Folded.StartsWith(needle, StringComparison.Ordinal))
            .ThenBy(c => c.Original, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Original)
            .ToList();

        return Ok(new { success = true, data = result, total = result.Count });
    }
}
