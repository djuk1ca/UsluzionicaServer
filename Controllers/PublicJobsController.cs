using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.Controllers;

/// <summary>
/// Poslovi za gosta — sajt ih prikazuje bez naloga (/pretraga, /posao/{id}).
///
/// Ista pravila vidljivosti kao <c>/api/jobs</c> za neprijavljenog gledaoca:
/// samo aktivni kojima rok nije istekao, bez uklonjenih i bez autora sa
/// ugašenim nalogom. Pretraga i detalj idu kroz <see cref="JobService"/>, pa se
/// filteri (podkategorije, preklopljen grad) ne razilaze.
///
/// Gledalac se NAMERNO ne čita iz tokena, čak i kad stigne: odgovor ne sme da
/// zavisi od toga ko pita (sajt ga kešira za sve posetioce), a oblik
/// <see cref="PublicJobDto"/> ionako nema polja za gledaoca.
/// </summary>
[ApiController]
[Route("api/public/jobs")]
[AllowAnonymous]
[EnableRateLimiting("public")]
public sealed class PublicJobsController(JobService jobs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] PublicJobQueryParams p)
    {
        var strana = await jobs.SearchAsync(new JobQueryParams
        {
            Q            = p.Q,
            City         = p.City,
            CategorySlug = p.CategorySlug,
            Boosted      = p.Boosted,
            Page         = p.Page,
            PageSize     = p.PageSize
        });

        return Ok(new
        {
            success = true,
            data = new PagedPublicJobs
            {
                Items    = strana.Items.Select(JobMapa.Javni).ToList(),
                Total    = strana.Total,
                Page     = strana.Page,
                PageSize = strana.PageSize
            }
        });
    }

    /// <summary>
    /// Detalj. Nepostojeći, dogovoren, istekao ili uklonjen posao — isti 404,
    /// da gost ne može da razazna koji je slučaj.
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var job = await jobs.GetAsync(id, viewerId: null);
        return job is null
            ? NotFound(new { success = false, message = "Ovaj posao više nije dostupan." })
            : Ok(new { success = true, data = JobMapa.Javni(job) });
    }
}
