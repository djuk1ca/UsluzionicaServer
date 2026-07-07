using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Categories;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class CategoryService(
    AppDbContext         db,
    ILogger<CategoryService> logger)
{
    // ── GET sve kao stablo ─────────────────────────────────────────────────
    /// <summary>
    /// Vraća kompletno stablo kategorija:
    ///   13 root kategorija, svaka sa popunjenim Children[].
    /// Klijent može odmah da renderuje dvonivojski meni.
    /// </summary>
    public async Task<List<CategoryDto>> GetTreeAsync()
    {
        // Učitavamo SVE kategorije jednim SQL upitom
        var all = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync();

        // Mapiramo u DTO rečnik, zatim gradimo stablo u memoriji
        var dtoById = all.ToDictionary(
            c => c.Id,
            c => new CategoryDto
            {
                Id        = c.Id,
                Name      = c.Name,
                Slug      = c.Slug,
                ParentId  = c.ParentId,
                SortOrder = c.SortOrder
            });

        var roots = new List<CategoryDto>();

        foreach (var dto in dtoById.Values)
        {
            if (dto.ParentId is null)
                roots.Add(dto);
            else if (dtoById.TryGetValue(dto.ParentId.Value, out var parent))
                parent.Children.Add(dto);
        }

        return roots;
    }

    // ── CREATE (admin) ─────────────────────────────────────────────────────
    public async Task<(CategoryDto? Result, string? Error)> CreateAsync(CreateCategoryDto dto)
    {
        // Slug mora biti jedinstven
        if (await db.Categories.AnyAsync(c => c.Slug == dto.Slug))
            return (null, $"Slug '{dto.Slug}' je već zauzet.");

        // Ako je naveden ParentId — proveravamo da parent postoji i da nije sam podkategorija
        if (dto.ParentId is not null)
        {
            var parent = await db.Categories.FindAsync(dto.ParentId.Value);
            if (parent is null)
                return (null, $"Parent kategorija sa Id={dto.ParentId} ne postoji.");
            if (parent.ParentId is not null)
                return (null, "Ne možeš kreirati podkategoriju podkategorije (max 2 nivoa).");
        }

        var category = new Category
        {
            Name      = dto.Name.Trim(),
            Slug      = dto.Slug.Trim().ToLowerInvariant(),
            ParentId  = dto.ParentId,
            SortOrder = dto.SortOrder
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync();

        logger.LogInformation("Kategorija kreirana: {Id} — {Name}", category.Id, category.Name);

        return (new CategoryDto
        {
            Id        = category.Id,
            Name      = category.Name,
            Slug      = category.Slug,
            ParentId  = category.ParentId,
            SortOrder = category.SortOrder
        }, null);
    }

    // ── UPDATE (admin) ─────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateAsync(int id, UpdateCategoryDto dto)
    {
        var category = await db.Categories.FindAsync(id);
        if (category is null)
            return (false, "Kategorija nije pronađena.");

        // Slug jedinstvenost — ignorišemo self-match
        if (await db.Categories.AnyAsync(c => c.Slug == dto.Slug && c.Id != id))
            return (false, $"Slug '{dto.Slug}' je već zauzet.");

        if (dto.ParentId is not null)
        {
            if (dto.ParentId == id)
                return (false, "Kategorija ne može biti sopstveni parent.");

            var parent = await db.Categories.FindAsync(dto.ParentId.Value);
            if (parent is null)
                return (false, $"Parent kategorija sa Id={dto.ParentId} ne postoji.");
            if (parent.ParentId is not null)
                return (false, "Ne možeš postaviti podkategoriju kao parent (max 2 nivoa).");
        }

        category.Name      = dto.Name.Trim();
        category.Slug      = dto.Slug.Trim().ToLowerInvariant();
        category.ParentId  = dto.ParentId;
        category.SortOrder = dto.SortOrder;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── DELETE (admin) ─────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> DeleteAsync(int id)
    {
        var category = await db.Categories
            .Include(c => c.Children)
            .Include(c => c.Listings)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (category is null)
            return (false, "Kategorija nije pronađena.");

        if (category.Listings.Count > 0)
            return (false, $"Kategorija ima {category.Listings.Count} listinga — najpre premesti ili obriši listinge.");

        if (category.Children.Count > 0)
            return (false, $"Kategorija ima {category.Children.Count} podkategorija — najpre obriši podkategorije.");

        db.Categories.Remove(category);
        await db.SaveChangesAsync();

        logger.LogInformation("Kategorija obrisana: {Id} — {Name}", id, category.Name);
        return (true, null);
    }
}
