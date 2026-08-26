using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    AuthService                  authService,
    ReferralService              referralService,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    // ── POST /api/auth/register ────────────────────────────────────────────
    /// <summary>
    /// Registruje novog korisnika i šalje verifikacioni email.
    /// Opcionalno polje referralCode za referral sistem.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting("email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (success, errors) = await authService.RegisterAsync(req);

        if (!success)
            return BadRequest(new { success = false, errors });

        return Ok(new
        {
            success = true,
            message = "Registracija uspešna. Proveri email i potvrdi nalog."
        });
    }

    // ── POST /api/auth/login ───────────────────────────────────────────────
    /// <summary>
    /// Prijavljuje korisnika. Vraća JWT access token (60 min) i refresh token (30 dana).
    /// IP adresa se koristi za ažuriranje LastKnownCity.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        // Uzimamo IP sa kojeg dolazi zahtev
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        var (response, error) = await authService.LoginAsync(req, ip);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = response });
    }

    // ── GET /api/auth/verify-email?userId=...&token=... ───────────────────
    /// <summary>
    /// Potvrđuje email adresu korisnika. Link stiže u emailu nakon registracije.
    /// Vraća HTML stranicu kako bi korisnik video poruku direktno u browseru.
    /// </summary>
    [HttpGet("verify-email")]
    [Produces("text/html")]
    public async Task<ContentResult> VerifyEmail([FromQuery] VerifyEmailRequest req)
    {
        var user = await userManager.FindByIdAsync(req.UserId);

        if (user is null)
            return HtmlPage(
                success: false,
                title:   "Nevažeći link",
                message: "Ovaj verifikacioni link nije ispravan. Pokušaj da se registruješ ponovo.");

        if (user.EmailConfirmed)
            return HtmlPage(
                success: true,
                title:   "Već potvrđen",
                message: "Tvoj email je već potvrđen. Možeš se prijaviti u aplikaciju.");

        var result = await userManager.ConfirmEmailAsync(user, req.Token);

        if (!result.Succeeded)
            return HtmlPage(
                success: false,
                title:   "Link je istekao",
                message: "Verifikacioni link je nevažeći ili je istekao (važi 24 sata). " +
                         "Prijavi se u aplikaciju i zatraži novi verifikacioni email.");

        // Prva referral rata — tek sada, kad je adresa dokazano stvarna.
        // Metoda je idempotentna: ovaj link se lako aktivira dvaput (mail
        // klijent ga prefetch-uje, pa korisnik klikne), a nagrada sme jednom.
        await referralService.TryRewardSignupAsync(user.Id);

        return HtmlPage(
            success: true,
            title:   "Email potvrđen!",
            message: "Tvoj nalog je uspešno aktiviran. Možeš se prijaviti u Uslužionica aplikaciju.");
    }

    // ── POST /api/auth/refresh ────────────────────────────────────────────
    /// <summary>
    /// Obnavlja access token pomoću refresh tokena.
    /// Stari refresh token se poništava i izdaje se novi (rotacija).
    /// </summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req)
    {
        var (response, error) = await authService.RefreshAsync(req.RefreshToken);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = response });
    }

    // ── POST /api/auth/logout ─────────────────────────────────────────────
    /// <summary>
    /// Odjavljuje korisnika — poništava refresh token u bazi.
    /// Access token ostaje validan do isteka (60 min) — klijent ga briše lokalno.
    /// </summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req)
    {
        await authService.LogoutAsync(req.RefreshToken);
        return Ok(new { success = true, message = "Odjavljen si." });
    }

    // ── POST /api/auth/resend-verification ───────────────────────────────
    /// <summary>
    /// Šalje novi verifikacioni email ako prethodni nije stigao ili je istekao.
    /// Uvek vraća 200 — ne otkrivamo da li email postoji.
    /// </summary>
    [HttpPost("resend-verification")]
    [EnableRateLimiting("email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationRequest req)
    {
        await authService.ResendVerificationAsync(req.Email);
        return Ok(new { success = true, message = "Ako nalog postoji i nije potvrđen, email je poslat." });
    }

    // ── POST /api/auth/forgot-password ───────────────────────────────────
    /// <summary>
    /// Šalje 6-cifreni kod za reset lozinke na email.
    /// Uvek vraća 200 — ne otkrivamo da li nalog postoji.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { success = false, message = "Unesi ispravnu email adresu." });

        await authService.ForgotPasswordAsync(req.Email);

        return Ok(new
        {
            success = true,
            message = "Ako nalog sa tom adresom postoji, kod je poslat na email."
        });
    }

    // ── POST /api/auth/reset-password ────────────────────────────────────
    /// <summary>
    /// Menja lozinku na osnovu koda iz emaila. Poništava sve aktivne sesije.
    /// </summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
    {
        if (!ModelState.IsValid)
        {
            var first = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault() ?? "Podaci nisu ispravni.";
            return BadRequest(new { success = false, message = first });
        }

        var (success, error) = await authService.ResetPasswordAsync(
            req.Email, req.Code, req.NewPassword);

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Lozinka je promenjena. Možeš se prijaviti." });
    }

    // ── HTML helper ────────────────────────────────────────────────────────
    private static ContentResult HtmlPage(bool success, string title, string message)
    {
        var color = success ? "#22C55E" : "#EF4444";
        var icon  = success ? "✓" : "✕";

        var html = $$"""
            <!DOCTYPE html>
            <html lang="sr">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>{{title}} — Uslužionica</title>
              <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  font-family: Inter, Arial, sans-serif;
                  background: #F7FAFC;
                  min-height: 100vh;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  padding: 24px;
                }
                .card {
                  background: #fff;
                  border-radius: 20px;
                  padding: 48px 40px;
                  max-width: 440px;
                  width: 100%;
                  text-align: center;
                  box-shadow: 0 4px 24px rgba(0,0,0,0.08);
                }
                .icon {
                  width: 72px; height: 72px; border-radius: 50%;
                  background: {{color}}18;
                  display: flex; align-items: center; justify-content: center;
                  margin: 0 auto 24px;
                  font-size: 32px; color: {{color}};
                }
                h1 { font-size: 22px; color: #0B1220; margin-bottom: 12px; }
                p  { font-size: 15px; color: #475569; line-height: 1.6; }
                .brand {
                  font-size: 13px; color: #94A3B8;
                  margin-top: 32px;
                  font-weight: 600;
                  letter-spacing: 0.03em;
                }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="icon">{{icon}}</div>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
                <div class="brand">USLUŽIONICA</div>
              </div>
            </body>
            </html>
            """;

        return new ContentResult
        {
            Content     = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode  = 200
        };
    }
}
