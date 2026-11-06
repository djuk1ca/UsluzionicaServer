using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Odjava sa liste čekanja — link iz mejla „stigli smo na App Store".
///
/// Bez naloga: na listi su ljudi koji naloga nemaju. Link nosi heš adrese i
/// potpis (<see cref="ListaCekanjaMejl.Potpis"/>), pa niko ne može da odjavi
/// tuđu adresu, a sama adresa nije u linku.
///
/// GET  = klik na link u mejlu → kratka strana „Odjavljen/a si".
/// POST = odjava jednim klikom iz Gmail-a/Yahoo-a (RFC 8058,
///        List-Unsubscribe-Post) — klijent šalje POST bez ikakvog sadržaja.
/// Obe su idempotentne: prefetch linka ili drugi klik ne smetaju.
/// </summary>
[ApiController]
[Route("api/lista-cekanja")]
[AllowAnonymous]
[EnableRateLimiting("public")]
public sealed class ListaCekanjaController(ListaCekanjaMejl mejl) : ControllerBase
{
    [HttpGet("odjava")]
    public async Task<IActionResult> OdjavaStrana([FromQuery] string? a, [FromQuery] string? p, CancellationToken ct)
    {
        var ok = a is not null && p is not null && await mejl.OdjaviAsync(a, p, ct);
        return Content(Strana(ok), "text/html; charset=utf-8");
    }

    [HttpPost("odjava")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OdjavaJednimKlikom([FromQuery] string? a, [FromQuery] string? p, CancellationToken ct)
    {
        var ok = a is not null && p is not null && await mejl.OdjaviAsync(a, p, ct);
        return ok ? Ok(new { success = true }) : BadRequest(new { success = false, message = "Link za odjavu nije ispravan." });
    }

    private static string Strana(bool ok)
    {
        var (naslov, tekst) = ok
            ? ("Odjavljen/a si",
               "Više ti ne šaljemo poruke sa liste čekanja. Ako otvoriš nalog istom adresom, bonus sa liste i dalje važi po istim pravilima.")
            : ("Link nije ispravan",
               "Ovaj link za odjavu nije potpun ili je izmenjen. Otvori ga ponovo iz mejla, ili nam piši na kontakt@usluzionica.rs.");

        return $"""
            <!DOCTYPE html>
            <html lang="sr">
            <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{naslov} — Uslužionica</title></head>
            <body style="font-family:Inter,Arial,sans-serif;background:#F7FAFC;margin:0;padding:32px 16px;">
              <div style="max-width:480px;margin:0 auto;background:#fff;border-radius:16px;padding:40px;box-shadow:0 2px 8px rgba(0,0,0,0.08);">
                <p style="margin:0 0 20px;font-size:18px;font-weight:800;color:#2F6BFF;">Uslužionica</p>
                <h2 style="color:#0B1220;margin:0 0 12px;">{naslov}</h2>
                <p style="color:#334155;line-height:1.6;margin:0;">{tekst}</p>
              </div>
            </body>
            </html>
            """;
    }
}
