using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Slika iz četa po POTPISANOM URL-u — img u WebView-u ne može da pošalje
/// Authorization header. Potpis, rok i učešće u razgovoru proverava
/// <see cref="ChatImageService.ProcitajAsync"/>.
/// </summary>
[ApiController]
[Route("api/chat-media")]
[AllowAnonymous]
public sealed class ChatMediaController(ChatImageService chatImages) : ControllerBase
{
    /// <summary>
    /// 404 za SVE neuspehe (loš ili istekao potpis, tuđi korisnik, obrisan
    /// fajl) — odgovor ne otkriva da prilog postoji.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] string? u, [FromQuery] long exp, [FromQuery] string? sig, CancellationToken ct)
    {
        var slika = await chatImages.ProcitajAsync(id, u ?? "", exp, sig ?? "", ct);
        if (slika is null) return NotFound();

        // Privatno keširanje do isteka URL-a: isti URL ceo sat (vidi
        // ChatMediaStore.PotpisanUrl), pa WebView ne preuzima sliku ponovo.
        Response.Headers.CacheControl = "private, max-age=3600";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.ContentSecurityPolicy = "sandbox";
        return File(slika, ChatImageService.VrstaSadrzaja(slika));
    }
}
