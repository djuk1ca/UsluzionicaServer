using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.Services;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Poslovi — korisnik objavljuje šta mu treba, uslugodavci se javljaju.
///
/// Sva pravila (ko sme šta i u kom stanju) žive u <see cref="JobService"/> i
/// <see cref="JobApplicationService"/>; kontroler samo prevodi rezultat u
/// HTTP. Sve traži prijavu — posao nosi grad i opis potrebe, pa se ne
/// izlaže anonimno kao oglas.
/// </summary>
[ApiController]
[Route("api/jobs")]
[Authorize]
public sealed class JobsController(
    JobService            jobs,
    JobApplicationService prijave,
    BoostService          boostService) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    // ── PRETRAGA I PRIKAZ ──────────────────────────────────────────────────

    /// <summary>Pretraga aktivnih poslova — isti parametri kao /api/listings.</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] JobQueryParams p)
    {
        p.ViewerUserId = UserId;
        return Ok(new { success = true, data = await jobs.SearchAsync(p) });
    }

    /// <summary>Moji poslovi: Aktivni · Dogovoreni · Istekli · „Za tebe".</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine() =>
        Ok(new { success = true, data = await jobs.MineAsync(UserId) });

    /// <summary>Poslovi na koje sam se javio i stanje javljanja.</summary>
    [HttpGet("applied")]
    public async Task<IActionResult> Applied() =>
        Ok(new { success = true, data = await jobs.AppliedAsync(UserId) });

    /// <summary>
    /// Detalj posla. Dogovoren vide samo autor i izabrani, istekao samo
    /// autor; ostali dobijaju 404 „Posao više nije dostupan".
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var job = await jobs.GetAsync(id, UserId);
        return job is null
            ? NotFound(new { success = false, message = "Ovaj posao više nije dostupan." })
            : Ok(new { success = true, data = job });
    }

    // ── AUTOR ──────────────────────────────────────────────────────────────

    /// <summary>Objava. Rate limit „jobs" — 10 objava dnevno po nalogu.</summary>
    [HttpPost]
    [EnableRateLimiting("jobs")]
    public async Task<IActionResult> Create([FromBody] CreateJobDto dto)
    {
        var (job, error) = await jobs.CreateAsync(UserId, dto);
        return error is not null
            ? BadRequest(new { success = false, message = error })
            : CreatedAtAction(nameof(Get), new { id = job!.Id }, new { success = true, data = job });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateJobDto dto) =>
        Rezultat(await jobs.UpdateAsync(id, UserId, dto), "Posao je izmenjen.");

    [HttpPost("{id:int}/images")]
    public async Task<IActionResult> UploadImage(int id, IFormFile file)
    {
        var (image, error) = await jobs.UploadImageAsync(id, UserId, file);
        return error is not null
            ? BadRequest(new { success = false, message = error })
            : StatusCode(StatusCodes.Status201Created, new { success = true, data = image });
    }

    [HttpDelete("{id:int}/images/{imageId:int}")]
    public async Task<IActionResult> DeleteImage(int id, int imageId) =>
        Rezultat(await jobs.DeleteImageAsync(id, imageId, UserId), "Slika je obrisana.");

    [HttpPost("{id:int}/extend")]
    public async Task<IActionResult> Extend(int id)
    {
        var (istice, error) = await jobs.ExtendAsync(id, UserId);
        return error is not null
            ? BadRequest(new { success = false, message = error })
            : Ok(new { success = true, data = new { expiresAt = istice } });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id) =>
        Rezultat(await jobs.DeleteAsync(id, UserId), "Posao je obrisan.");

    /// <summary>Isticanje — iste cene i trajanja kao za oglase.</summary>
    [HttpPost("{id:int}/boost")]
    public async Task<IActionResult> Boost(int id, [FromBody] BoostJobDto dto) =>
        Rezultat(await boostService.BoostJobAsync(id, UserId, dto), "Posao je istaknut.");

    /// <summary>Javljanja na moj posao. sort = newest | rating | offer.</summary>
    [HttpGet("{id:int}/applications")]
    public async Task<IActionResult> Applications(int id, [FromQuery] string? sort)
    {
        var (items, error) = await prijave.ListAsync(id, UserId, sort);
        return error is not null
            ? NotFound(new { success = false, message = error })
            : Ok(new { success = true, data = items });
    }

    [HttpPost("{id:int}/assign")]
    public async Task<IActionResult> Assign(int id, [FromBody] AssignJobDto dto)
    {
        var (razgovor, error) = await prijave.AssignAsync(id, UserId, dto.ApplicationId);
        return error is not null
            ? BadRequest(new { success = false, message = error })
            : Ok(new { success = true, data = new { conversationId = razgovor } });
    }

    [HttpPost("{id:int}/unassign")]
    public async Task<IActionResult> Unassign(int id, [FromBody] UnassignJobDto? dto) =>
        Rezultat(await prijave.UnassignAsync(id, UserId, dto?.Reason), "Posao je ponovo aktivan.");

    [HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id)
    {
        var (nagrada, error) = await prijave.CompleteAsync(id, UserId);
        return error is not null
            ? BadRequest(new { success = false, message = error })
            : Ok(new { success = true, data = new { reward = nagrada } });
    }

    // ── USLUGODAVAC ────────────────────────────────────────────────────────

    [HttpPost("{id:int}/applications")]
    public async Task<IActionResult> Apply(int id, [FromBody] ApplyToJobDto dto)
    {
        var (prijava, razgovor, error) = await prijave.ApplyAsync(id, UserId, dto);
        return error is not null
            ? BadRequest(new { success = false, message = error })
            : Ok(new { success = true, data = new { application = prijava, conversationId = razgovor } });
    }

    [HttpDelete("{id:int}/applications/mine")]
    public async Task<IActionResult> Withdraw(int id) =>
        Rezultat(await prijave.WithdrawAsync(id, UserId), "Javljanje je povučeno.");

    [HttpPost("{id:int}/quit")]
    public async Task<IActionResult> Quit(int id) =>
        Rezultat(await prijave.QuitAsync(id, UserId), "Odustao si od posla.");

    private ObjectResult Rezultat((bool Ok, string? Error) r, string poruka) =>
        r.Ok
            ? Ok(new { success = true, message = poruka })
            : BadRequest(new { success = false, message = r.Error });
}
