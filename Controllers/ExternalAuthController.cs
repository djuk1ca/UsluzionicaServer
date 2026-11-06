using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Infrastructure.Apple;
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
///
/// SAJT ide istim tokom, sa <c>client=web</c> na startu: povratak je tada
/// <c>{App:SiteUrl}/app/oauth/povratak#code=…</c>, a razmenu sajt radi sa svog
/// servera, istom PKCE tajnom. Klijent putuje u potpisanom state-u, pa ga
/// provajder ni napadač ne mogu promeniti.
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class ExternalAuthController(
    IEnumerable<IExternalAuthProvider> providers,
    ExternalAuthTickets                tickets,
    ExternalLoginService               externalLogin,
    AuthService                        authService,
    UserManager<ApplicationUser>       userManager,
    IAppleSignIn                       apple,
    AppleNalog                         appleNalog,
    IConfiguration                     config,
    ILogger<ExternalAuthController>    logger) : ControllerBase
{
    /// <summary>Kuda se aplikacija vraća. Mora se poklapati sa IntentFilter-om na Androidu.</summary>
    private string AppCallback =>
        config["ExternalAuth:CallbackUri"] is { Length: > 0 } uri ? uri : "usluzionica://auth";

    /// <summary>Kuda se vraća sajt. Podrazumevano strana sajta koja čita fragment.</summary>
    private string WebCallback =>
        config["ExternalAuth:WebCallbackUri"] is { Length: > 0 } uri
            ? uri
            : $"{(config["App:SiteUrl"] is { Length: > 0 } sajt ? sajt : "https://usluzionica.rs").TrimEnd('/')}/app/oauth/povratak";

    /// <summary>Jedina vrednost <c>client</c> koja menja povratak; sve ostalo ide u aplikaciju.</summary>
    private const string WebKlijent = "web";

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
            data    = providers.Where(p => p.IsConfigured).Select(p => p.Name)
                .Concat(apple.Podesen ? [AppleNalog.Provajder] : [])
                .ToList()
        });

    // ── GET /api/auth/{provider}/start ─────────────────────────────────────
    [HttpGet("{provider}/start")]
    [EnableRateLimiting("oauth")]
    public IActionResult Start(
        string provider,
        [FromQuery] string? challenge,
        [FromQuery(Name = "ref")] string? referralCode,
        [FromQuery] string? client)
    {
        var klijent = NormalizujKlijenta(client);

        var p = Nadji(provider);
        if (p is null || !p.IsConfigured)
            return Povratak(klijent, greska: Greske.NijePodeseno);

        // Bez ispravnog challenge-a razmena kasnije ne bi mogla da uspe — bolje
        // odmah vratiti korisnika nego ga poslati kroz Facebook uzalud.
        if (!ExternalAuthTickets.IsValidChallenge(challenge))
            return Povratak(klijent, greska: Greske.Opsta);

        var state = tickets.ProtectState(new(p.Name, challenge!, NormalizujReferral(referralCode), klijent));

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
            return Povratak(null, greska: Greske.Opsta);

        // State čuva od podmetanja: bez njega bi napadač mogao da natera tuđu
        // aplikaciju da završi prijavu u NAPADAČEV nalog (login CSRF).
        // Čita se PRE provere `error`, da i otkazivanje vrati sajt na sajt.
        // Neispravan state → nema ni klijenta, povratak ide u aplikaciju.
        var st = string.IsNullOrEmpty(state) ? null : tickets.UnprotectState(state);
        if (st is not null && st.Provider != p.Name) st = null;
        var klijent = NormalizujKlijenta(st?.Client);

        // Korisnik je na Google-ovoj/Facebook-ovoj stranici odbio ili zatvorio.
        if (!string.IsNullOrEmpty(error))
            return Povratak(klijent, greska: Greske.Otkazano);

        if (st is null || string.IsNullOrEmpty(code))
            return Povratak(klijent, greska: Greske.Opsta);

        ExternalIdentity identitet;
        try
        {
            identitet = await p.ExchangeCodeAsync(code, RedirectUri(p), ct);
        }
        catch (Exception ex) when (ex is ExternalAuthException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Razmena koda sa {Provider} nije uspela.", p.Name);
            return Povratak(klijent, greska: Greske.Opsta);
        }

        var ishod = await externalLogin.RazresiAsync(identitet);

        return ishod switch
        {
            Prijavljen pr => Povratak(klijent, kod: tickets.ProtectResult(
                new(st.Challenge, pr.User.Id, null, null))),

            TrebaRegistracija => Povratak(klijent, kod: tickets.ProtectResult(
                new(st.Challenge, null, identitet, st.ReferralCode))),

            Odbijen od => Povratak(klijent, greska: od.Greska),

            _ => Povratak(klijent, greska: Greske.Opsta)
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

        // Apple: refresh token je čekao u karti dok nalog ne nastane.
        if (karta.AppleRefreshToken is { } zasticen)
            await appleNalog.SacuvajAsync(user, zasticen, vecZasticen: true);

        var auth = await authService.IssueTokensAsync(user);
        return Ok(new { success = true, data = auth });
    }

    // ── Pomoćne ────────────────────────────────────────────────────────────

    // ── POST /api/auth/apple ───────────────────────────────────────────────
    /// <summary>
    /// Sign in with Apple sa iPhone-a. Izvorni Apple prozor vraća aplikaciji
    /// identityToken (JWT od Apple-a) — nema preusmeravanja kroz server kao kod
    /// Google-a, pa ni PKCE karte. Ishod je isti oblik kao /external/exchange,
    /// da bi aplikacija posle prijave radila jednako za sve provajdere.
    ///
    /// Odbijanje vraća `code` (nema-email, deaktiviran, nalog-postoji…) — isti
    /// kodovi kao u povratnoj adresi Google prijave, aplikacija ih već prevodi.
    /// </summary>
    [HttpPost("apple")]
    [EnableRateLimiting("oauth")]
    [ProducesResponseType(typeof(ExternalExchangeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Apple([FromBody] AppleSignInRequest req, CancellationToken ct)
    {
        if (!apple.Podesen)
            return BadRequest(new { success = false, code = Greske.NijePodeseno,
                                    message = "Prijava preko Apple-a trenutno nije dostupna." });

        var id = await apple.ProveriAsync(req.IdentityToken, ct);
        if (id is null)
            return BadRequest(new { success = false, code = Greske.Opsta,
                                    message = "Prijava preko Apple-a nije uspela. Pokušaj ponovo." });

        // Apple garantuje mejl koji šalje (i relej) — EmailVerified sledi njegov claim.
        var identitet = new ExternalIdentity(
            AppleNalog.Provajder, id.Sub, id.Email, id.EmailPotvrdjen, req.FullName?.Trim() ?? "");

        var ishod = await externalLogin.RazresiAsync(identitet);
        if (ishod is Odbijen odbijen)
            return BadRequest(new { success = false, code = odbijen.Greska,
                                    message = PorukaOdbijanja(odbijen.Greska) });

        // Kod važi 5 minuta i jednom — menja se odmah, i za postojeći nalog
        // (osveži token) i za nov (čeka u karti).
        var refresh = await apple.RefreshTokenAsync(req.AuthorizationCode ?? "", ct);

        if (ishod is Prijavljen prijavljen)
        {
            await appleNalog.SacuvajAsync(prijavljen.User, refresh);
            var auth = await authService.IssueTokensAsync(prijavljen.User);
            return Ok(new { success = true, data = new ExternalExchangeResponse { Status = "ok", Auth = auth } });
        }

        return Ok(new
        {
            success = true,
            data    = new ExternalExchangeResponse
            {
                Status      = "signup",
                SignupToken = tickets.ProtectSignup(new(identitet, NormalizujReferral(req.ReferralCode),
                                  refresh is null ? null : appleNalog.Zasticen(refresh))),
                FullName    = identitet.Name,
                Email       = identitet.Email,
                Provider    = identitet.Provider
            }
        });
    }

    /// <summary>
    /// Isti tekstovi kao u aplikaciji za Google tok (SocijalnaPrijava.PorukaZa) —
    /// ovde jer Apple tok odgovara JSON-om, a aplikacija prikazuje `message`.
    /// </summary>
    private static string PorukaOdbijanja(string kod) => kod switch
    {
        Greske.NemaEmail    => "Nalog nema email adresu, ili nisi dozvolio da je podeli. Prijavi se drugim načinom.",
        Greske.NalogPostoji => "Nalog sa ovom email adresom već postoji. Prijavi se načinom kojim si ga napravio.",
        Greske.Deaktiviran  => "Nalog je deaktiviran. Kontaktiraj podršku.",
        _                   => "Prijava preko Apple-a nije uspela. Pokušaj ponovo."
    };

    private IExternalAuthProvider? Nadji(string name) =>
        providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private string RedirectUri(IExternalAuthProvider p) =>
        $"{(config["App:BaseUrl"] ?? string.Empty).TrimEnd('/')}/api/auth/{p.Name}/callback";

    /// <summary>
    /// Nazad u aplikaciju ili na sajt. Rezultat ide u FRAGMENT (#), ne u query (?):
    /// pregledač fragment ne šalje nijednom serveru i ne upisuje ga u
    /// Referer zaglavlje.
    ///
    /// Adresa je uvek iz konfiguracije, nikad iz zahteva — <c>client</c> samo bira
    /// jednu od dve, pa ovo ne može postati open redirect.
    /// </summary>
    private RedirectResult Povratak(string? klijent, string? kod = null, string? greska = null)
    {
        var adresa = klijent == WebKlijent ? WebCallback : AppCallback;
        return Redirect(kod is not null
            ? $"{adresa}#code={Uri.EscapeDataString(kod)}"
            : $"{adresa}#error={Uri.EscapeDataString(greska ?? Greske.Opsta)}");
    }

    /// <summary>„web" ili null (aplikacija). Nepoznata vrednost je aplikacija.</summary>
    private static string? NormalizujKlijenta(string? client) =>
        string.Equals(client?.Trim(), WebKlijent, StringComparison.OrdinalIgnoreCase) ? WebKlijent : null;

    private static string? NormalizujReferral(string? code) =>
        string.IsNullOrWhiteSpace(code) || code.Length > 20
            ? null
            : code.Trim().ToUpperInvariant();
}
