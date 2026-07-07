using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Categories;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/categories")]
public sealed class CategoriesController(CategoryService categoryService) : ControllerBase
{
    // ── GET /api/categories ───────────────────────────────────────────────
    /// <summary>
    /// Vraća sve kategorije kao dvonivojsko stablo.
    /// Javni endpoint — ne zahteva autentifikaciju.
    /// Odgovor: lista root kategorija, svaka ima Children[] sa podkategorijama.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var tree = await categoryService.GetTreeAsync();
        return Ok(new { success = true, data = tree, total = tree.Count });
    }

    // ── POST /api/categories ──────────────────────────────────────────────
    /// <summary>
    /// Kreira novu kategoriju. Samo Admin.
    /// Body: { name, slug, parentId?, sortOrder }
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateCategoryDto dto)
    {
        var (result, error) = await categoryService.CreateAsync(dto);
        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return CreatedAtAction(nameof(GetAll), new { success = true, data = result });
    }

    // ── PUT /api/categories/{id} ──────────────────────────────────────────
    /// <summary>
    /// Ažurira postojeću kategoriju. Samo Admin.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCategoryDto dto)
    {
        var (success, error) = await categoryService.UpdateAsync(id, dto);

        if (!success && error == "Kategorija nije pronađena.")
            return NotFound(new { success = false, message = error });

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Kategorija ažurirana." });
    }

    // ── DELETE /api/categories/{id} ───────────────────────────────────────
    /// <summary>
    /// Briše kategoriju. Samo Admin.
    /// Blokirano ako kategorija ima aktivne listinge ili podkategorije.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id)
    {
        var (success, error) = await categoryService.DeleteAsync(id);

        if (!success && error == "Kategorija nije pronađena.")
            return NotFound(new { success = false, message = error });

        if (!success)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, message = "Kategorija obrisana." });
    }
}
