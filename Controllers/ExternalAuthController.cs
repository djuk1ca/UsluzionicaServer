using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Infrastructure.ExternalAuth;
using UsluzionicaServer.Services;
using static UsluzionicaServer.Services.ExternalLoginService;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Prijava i registracija preko Google-a i Facebook-a.
///
/// TOK, KORAK PO KORAK
///
///   1. Aplikacija otvori <c>GET /api/auth/{provider}/start?challenge=…</c> u
///      sistemskom pregledaču (Chrome Custom Tab preko aplikacije).
///   2. Server preusmeri na Google/Facebook, sa potpisanim <c>state</c>.
///   3. Korisnik se prijavi kod provajdera — NJIHOVA stranica, njihova lozinka;
///      aplikacija je nikad ne vidi.
///   4. Provajder vrati na <c>/api/auth/{provider}/callback</c>. Server
///      razmeni kod (sa tajnom aplikacije) i razreši nalog.
///   5. Server preusmeri na <c>usluzionica://auth#code=…</c>; prozor se zatvori.
///   6. Aplikacija pošalje kod + PKCE tajnu na <c>external/exchange</c> i dobije
///      tokene — ili, za nov nalog, kartu za „Dovrši nalog".
///
/// Tokeni NIKAD ne putuju kroz adresu. Povratna adresa nosi samo kratkoživeći
/// kod koji bez PKCE tajne ne vredi ništa.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class ExternalAuthController(
    IEnumerable<IExternalAuthProvider> providers,
    ExternalAuthTickets                tickets,
    ExternalLoginService               externalLogin,
    AuthService                        authService,
    UserManager<ApplicationUser>       userManager,
    IConfiguration                     config,
    ILogger<ExternalAuthController>    logger) : ControllerBase
{
    /// <summary>Kuda se aplikacija vraća. Mora se poklapati sa IntentFilter-om na Androidu.</summary>
    private string AppCallback =>
        config["ExternalAuth:CallbackUri"] is { Length: > 0 } uri ? uri : "usluzionica://auth";

    // ── GET /api/auth/providers ────────────────────────────────────────────
    /// <summary>
    /// Koji provajderi su trenutno uključeni — aplikacija prikazuje samo njihova
    /// dugmad. Tako se Facebook pali i gasi kroz .env servera, bez novog izdanja
    /// aplikacije i bez čekanja na Play review.
    /// </summary>
    [HttpGet("providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Providers() =>
        Ok(new
        {
            success = true,
            data    = providers.Where(p => p.IsConfigured).Select(p => p.Name).ToList()
        });

    // ── GET /api/auth/{provider}/start ─────────────────────────────────────
    [HttpGet("{provider}/start")]
    [EnableRateLimiting("oauth")]
    public IActionResult Start(
        string provider,
        [FromQuery] string? challenge,
        [FromQuery(Name = "ref")] string? referralCode)
    {
        var p = Nadji(provider);
        if (p is null || !p.IsConfigured)
            return NaAplikaciju(greska: Greske.NijePodeseno);

        // Bez ispravnog challenge-a razmena kasnije ne bi mogla da uspe — bolje
        // odmah vratiti korisnika nego ga poslati kroz Facebook uzalud.
        if (!ExternalAuthTickets.IsValidChallenge(challenge))
            return NaAplikaciju(greska: Greske.Opsta);

        var state = tickets.ProtectState(new(p.Name, challenge!, NormalizujReferral(referralCode)));

        return Redirect(p.BuildAuthorizeUrl(state, RedirectUri(p)));
    }

    // ── GET /api/auth/{provider}/callback ──────────────────────────────────
    // Ovu adresu zove PROVAJDER, ne aplikacija. Mora biti upisana TAČNO ovako u
    // Meta konzoli (Valid OAuth Redirect URIs) i u Google Cloud-u (Authorized
    // redirect URIs) — sa strict mode-om svako odstupanje obara prijavu.
    [HttpGet("{provider}/callback")]
    [EnableRateLimiting("oauth")]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        var p = Nadji(provider);
        if (p is null)
            return NaAplikaciju(greska: Greske.Opsta);

        // Korisnik je na Google-ovoj/Facebook-ovoj stranici odbio ili zatvorio.
        if (!string.IsNullOrEmpty(error))
            return NaAplikaciju(greska: Greske.Otkazano);

        // State čuva od podmetanja: bez njega bi napadač mogao da natera tuđu
        // aplikaciju da završi prijavu u NAPADAČEV nalog (login CSRF).
        var st = string.IsNullOrEmpty(state) ? null : tickets.UnprotectState(state);
        if (st is null || st.Provider != p.Name || string.IsNullOrEmpty(code))
            return NaAplikaciju(greska: Greske.Opsta);

        ExternalIdentity identitet;
        try
        {
            identitet = await p.ExchangeCodeAsync(code, RedirectUri(p), ct);
        }
        catch (Exception ex) when (ex is ExternalAuthException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Razmena koda sa {Provider} nije uspela.", p.Name);
            return NaAplikaciju(greska: Greske.Opsta);
        }

        var ishod = await externalLogin.RazresiAsync(identitet);

        return ishod switch
        {
            Prijavljen pr => NaAplikaciju(kod: tickets.ProtectResult(
                new(st.Challenge, pr.User.Id, null, null))),

            TrebaRegistracija => NaAplikaciju(kod: tickets.ProtectResult(
                new(st.Challenge, null, identitet, st.ReferralCode))),

            Odbijen od => NaAplikaciju(greska: od.Greska),

            _ => NaAplikaciju(greska: Greske.Opsta)
        };
    }

    // ── POST /api/auth/external/exchange ───────────────────────────────────
    [HttpPost("external/exchange")]
    [EnableRateLimiting("oauth")]
    [ProducesResponseType(typeof(ExternalExchangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Exchange([FromBody] ExternalExchangeRequest req)
    {
        const string istekao = "Prijava je istekla. Pokušaj ponovo.";

        if (!ExternalAuthTickets.IsValidVerifier(req.Verifier))
            return BadRequest(new { success = false, message = istekao });

        var rez = tickets.UnprotectResult(req.Code);
        if (rez is null || !ExternalAuthTickets.VerifierMatches(req.Verifier, rez.Challenge))
            return BadRequest(new { success = false, message = istekao });

        if (rez.UserId is not null)
        {
            // Stanje se proverava PONOVO: između povratka sa Google-a i razmene
            // mogao je da prođe trenutak u kom je admin deaktivirao nalog.
            var user = await userManager.FindByIdAsync(rez.UserId);
            if (user is null || !user.IsActive)
                return BadRequest(new { success = false, message = "Nalog je deaktiviran. Kontaktiraj podršku." });

            var auth = await authService.IssueTokensAsync(user);

            return Ok(new
            {
                success = true,
                data    = new ExternalExchangeResponse { Status = "ok", Auth = auth }
            });
        }

        if (rez.Identity is null)
            return BadRequest(new { success = false, message = istekao });

        return Ok(new
        {
            success = true,
            data    = new ExternalExchangeResponse
            {
                Status      = "signup",
                SignupToken = tickets.ProtectSignup(new(rez.Identity, rez.ReferralCode)),
                FullName    = rez.Identity.Name,
                Email       = rez.Identity.Email,
                Provider    = rez.Identity.Provider,
                PictureUrl  = rez.Identity.PictureUrl
            }
        });
    }

    // ── POST /api/auth/external/register ───────────────────────────────────
    [HttpPost("external/register")]
    [EnableRateLimiting("oauth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] ExternalRegisterRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { success = false, message = "Proveri unete podatke." });

        var karta = tickets.UnprotectSignup(req.SignupToken);
        if (karta is null)
            return BadRequest(new { success = false, message = "Vreme za dovršavanje naloga je isteklo. Prijavi se ponovo." });

        var (user, greska) = await externalLogin.RegistrujAsync(karta.Identity, req, karta.ReferralCode);
        if (user is null)
            return BadRequest(new { success = false, message = greska });

        var auth = await authService.IssueTokensAsync(user);
        return Ok(new { success = true, data = auth });
    }

    // ── Pomoćne ────────────────────────────────────────────────────────────

    private IExternalAuthProvider? Nadji(string name) =>
        providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private string RedirectUri(IExternalAuthProvider p) =>
        $"{(config["App:BaseUrl"] ?? string.Empty).TrimEnd('/')}/api/auth/{p.Name}/callback";

    /// <summary>
    /// Nazad u aplikaciju. Rezultat ide u FRAGMENT (#), ne u query (?):
    /// pregledač fragment ne šalje nijednom serveru i ne upisuje ga u
    /// Referer zaglavlje.
    /// </summary>
    private RedirectResult NaAplikaciju(string? kod = null, string? greska = null) =>
        Redirect(kod is not null
            ? $"{AppCallback}#code={Uri.EscapeDataString(kod)}"
            : $"{AppCallback}#error={Uri.EscapeDataString(greska ?? Greske.Opsta)}");

    private static string? NormalizujReferral(string? code) =>
        string.IsNullOrWhiteSpace(code) || code.Length > 20
            ? null
            : code.Trim().ToUpperInvariant();
}
