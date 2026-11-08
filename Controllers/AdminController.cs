using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    BookingService        bookingService,
    ReportService         reportService,
    UserModerationService userModeration,
    ChatImageService      chatImages) : ControllerBase
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

    /// <summary>
    /// Pouzdan nalog: kad su oba učesnika termina pouzdana, čekanje do
    /// „Izvršeno" se preskače pri potvrdi. Stanje se šalje eksplicitno (kao kod
    /// deaktivacije), da dupli klik ne obrne odluku.
    /// </summary>
    [HttpPatch("users/{id}/trusted")]
    public async Task<IActionResult> SetUserTrusted(string id, [FromQuery] bool trusted = true)
    {
        if (!await adminService.SetTrustedAsync(id, trusted))
            return NotFound(new { success = false, message = "Korisnik nije pronađen." });

        return Ok(new { success = true });
    }

    // ── ZAKAZANI TERMINI: ČEKANJE DO IZVRŠENJA ─────────────────────────────

    /// <summary>
    /// Potvrđeni termini. <c>waiting=true</c> (podrazumevano) vraća samo one
    /// kojima „Izvršeno" još nije dozvoljeno.
    /// </summary>
    [HttpGet("bookings/confirmed")]
    public async Task<IActionResult> GetConfirmedBookings(
        [FromQuery] int  page     = 1,
        [FromQuery] int  pageSize = 20,
        [FromQuery] bool waiting  = true)
    {
        var (items, total) = await bookingService.GetConfirmedForAdminAsync(page, pageSize, waiting);
        return Ok(new { success = true, data = items, total, page, pageSize });
    }

    /// <summary>
    /// Skida čekanje: uslugodavac može odmah da označi uslugu kao izvršenu.
    /// Nagrade i ocene ostaju iste; beleži se koji admin je to uradio i kada.
    /// </summary>
    [HttpPost("bookings/{id:int}/allow-execute-now")]
    public async Task<IActionResult> AllowExecuteNow(int id)
    {
        var (ok, greska, nePostoji) = await bookingService.SkiniCekanjeAsync(id, AdminId);
        if (nePostoji) return NotFound(new { success = false, message = greska });
        if (!ok)       return BadRequest(new { success = false, message = greska });
        return Ok(new { success = true });
    }

    /// <summary>Vraća podrazumevano čekanje (poništava allow-execute-now).</summary>
    [HttpDelete("bookings/{id:int}/allow-execute-now")]
    public async Task<IActionResult> RestoreExecuteWait(int id)
    {
        var (ok, greska, nePostoji) = await bookingService.VratiCekanjeAsync(id, AdminId);
        if (nePostoji) return NotFound(new { success = false, message = greska });
        if (!ok)       return BadRequest(new { success = false, message = greska });
        return Ok(new { success = true });
    }

    private string AdminId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

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
            return NotFound(new { success = false, message = "Profil uslugodavca nije pronađen." });

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

    // ── LISTA ČEKANJA ──────────────────────────────────────────────────────

    /// <summary>
    /// Stanje bonusa za listu čekanja: koliko adresa server vidi na listi i
    /// koliko je bonusa isplaćeno. Samo brojevi — nijedna adresa.
    ///
    /// Prvo što treba pogledati posle deploya: `naListi = 0` uz `ukljucen =
    /// true` znači da API ne vidi fajl sajta (volumen nije montiran).
    /// </summary>
    [HttpGet("waitlist")]
    public async Task<IActionResult> GetWaitlist([FromServices] ListaCekanjaBonus bonus)
    {
        var s = await bonus.StanjeAsync();
        return Ok(new
        {
            success = true,
            data = new
            {
                ukljucen  = s.Ukljucen,
                iznos     = s.Iznos,
                isplaceno = s.Isplaceno,
                android   = new { naListi = s.Android.NaListi, izlazak = s.Android.Izlazak, rok = s.Android.Rok },
                ios       = new { naListi = s.Ios.NaListi,     izlazak = s.Ios.Izlazak,     rok = s.Ios.Rok }
            }
        });
    }

    /// <summary>
    /// Pokreće prolaz kroz listu odmah, umesto da se čeka sledećih 30 minuta.
    /// Bezbedno je pozvati više puta — bonus se isplaćuje jednom po adresi.
    /// </summary>
    [HttpPost("waitlist/run")]
    public async Task<IActionResult> RunWaitlist([FromServices] ListaCekanjaBonus bonus)
    {
        var isplaceno = await bonus.ProdjiAsync(HttpContext.RequestAborted);
        return Ok(new { success = true, data = new { isplaceno } });
    }

    /// <summary>
    /// Mejl „Uslužionica je na App Store-u" svima sa liste čekanja.
    ///
    /// PODRAZUMEVANO JE DRY-RUN: bez <c>posalji=true</c> ne odlazi nijedna
    /// poruka — vraća se broj primalaca, šta se preskače i zašto, i pregled
    /// mejla (naslov, HTML, tekst). Nijedna adresa se ne vraća.
    ///
    /// Pravo slanje: <c>posalji=true</c>. Šalje najviše <c>max</c> poruka
    /// (podrazumevano Waitlist:Email:MaxPerRun), u paketima sa pauzom; ponovno
    /// pokretanje nastavlja gde je stalo i nikome ne šalje dvaput. Detalji i
    /// pravila: <see cref="ListaCekanjaMejl"/>.
    /// </summary>
    [HttpPost("waitlist/launch-email")]
    [EnableRateLimiting("admin-mail")]
    public async Task<IActionResult> LaunchEmail(
        [FromServices] ListaCekanjaMejl mejl,
        [FromQuery] bool    posalji        = false,
        [FromQuery] string? lista          = null,
        [FromQuery] int?    max            = null,
        [FromQuery] bool    ponoviNeuspele = false)
    {
        try
        {
            var izvestaj = await mejl.PokreniAsync(
                new ListaCekanjaMejl.Opcije(posalji, string.IsNullOrWhiteSpace(lista) ? null : lista.Trim().ToLowerInvariant(),
                                            max, ponoviNeuspele),
                HttpContext.RequestAborted);
            return Ok(new { success = true, data = izvestaj });
        }
        catch (ListaCekanjaMejl.Odbijeno ex)
        {
            return Conflict(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Probna poruka na JEDNU adresu (npr. svoju), da se mejl vidi u pravom
    /// inboksu pre slanja listi. Ne računa se kao poslato.
    /// </summary>
    [HttpPost("waitlist/launch-email/test")]
    [EnableRateLimiting("admin-mail")]
    public async Task<IActionResult> LaunchEmailTest(
        [FromServices] ListaCekanjaMejl mejl,
        [FromBody] LaunchEmailTestDto dto)
    {
        try
        {
            var naslov = await mejl.ProbaAsync(dto.Email, dto.SaBonusom);
            return Ok(new { success = true, data = new { naslov } });
        }
        catch (ListaCekanjaMejl.Odbijeno ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
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
        [FromQuery] string?          reportedUserId = null,
        [FromQuery] int?             jobId          = null)
    {
        var detalji = await reportService.DetaljiAsync(targetType, listingId, reportedUserId, jobId);
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

    // ── GET /api/admin/messages/{id}/image ────────────────────────────────
    /// <summary>
    /// Slika iz prijavljene poruke („Prikaži sliku iz poruke"). Prijava je
    /// zadržava 30 dana i posle roka od 14; isti dešifrator, bez potpisanog URL-a.
    /// </summary>
    [HttpGet("messages/{id:int}/image")]
    public async Task<IActionResult> GetMessageImage(int id, CancellationToken ct)
    {
        var slika = await chatImages.ProcitajZaAdminaAsync(id, ct);
        if (slika is null) return NotFound(new { success = false, message = "Slika više nije dostupna." });

        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(slika, ChatImageService.VrstaSadrzaja(slika));
    }
}
