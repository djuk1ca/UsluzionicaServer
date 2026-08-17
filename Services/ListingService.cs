using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class ListingService(
    AppDbContext                 db,
    UserManager<ApplicationUser> userManager,
    IWebHostEnvironment          env,
    IConfiguration               config,
    ILogger<ListingService>      logger)
{
    // ── SEARCH (javno) ─────────────────────────────────────────────────────
    /// <summary>
    /// Pretražuje aktivne listinge sa opcionalnim filterima.
    /// Sortiranje: boosted prvo, zatim boost score, zatim datum.
    /// </summary>
    public async Task<PagedResult<ListingDto>> SearchAsync(ListingQueryParams p)
    {
        // Normalizuj PageSize — min 1, max 50
        p.PageSize = Math.Clamp(p.PageSize, 1, 50);
        p.Page     = Math.Max(p.Page, 1);

        var query = db.Listings
            .AsNoTracking()
            .Include(l => l.Category)
            .Include(l => l.Images)
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .Where(l => l.Status == ListingStatus.Active);

        // Fulltext filter — naslov i opis
        if (!string.IsNullOrWhiteSpace(p.Q))
        {
            var q = p.Q.Trim();
            query = query.Where(l =>
                EF.Functions.Like(l.Title,       $"%{q}%") ||
                EF.Functions.Like(l.Description, $"%{q}%"));
        }

        // Filter po kategoriji (slug)
        if (!string.IsNullOrWhiteSpace(p.CategorySlug))
        {
            var slug = p.CategorySlug.Trim().ToLowerInvariant();
            // Uzimamo i podkategorije: ako je slug parent kategorije, uključujemo sve njene childrene
            var categoryIds = await db.Categories
                .Where(c => c.Slug == slug || (c.Parent != null && c.Parent.Slug == slug))
                .Select(c => c.Id)
                .ToListAsync();

            if (categoryIds.Count > 0)
                query = query.Where(l => categoryIds.Contains(l.CategoryId));
        }

        // Filter po gradu
        if (!string.IsNullOrWhiteSpace(p.City))
        {
            var city = p.City.Trim();
            query = query.Where(l => l.Location == city);
        }

        // Ukupan broj pre paginacije
        var total = await query.CountAsync();

        // Sortiranje: boosted → boost score → najnoviji
        var items = await query
            .OrderByDescending(l => l.IsBoosted)
            .ThenByDescending(l => l.BoostScore)
            .ThenByDescending(l => l.CreatedAt)
            .Skip((p.Page - 1) * p.PageSize)
            .Take(p.PageSize)
            .ToListAsync();

        return new PagedResult<ListingDto>
        {
            Items    = items.Select(MapToDto).ToList(),
            Total    = total,
            Page     = p.Page,
            PageSize = p.PageSize
        };
    }

    // ── GET BY ID (javno, uvećava ViewCount) ───────────────────────────────
    /// <summary>
    /// Vraća detalje listinga. ViewCount se uvećava samo ako gledalac NIJE vlasnik
    /// (anoniman ili drugi korisnik = pravi pregled).
    /// </summary>
    public async Task<ListingDto?> GetByIdAsync(int id, string? viewerUserId = null)
    {
        var listing = await db.Listings
            .Include(l => l.Category)
            .Include(l => l.Images.OrderBy(i => i.SortOrder))
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .FirstOrDefaultAsync(l => l.Id == id && l.Status != ListingStatus.Archived);

        if (listing is null) return null;

        // Ne broj svoje preglede (vlasnik koji otvara svoj oglas)
        if (viewerUserId != listing.ProviderProfile.UserId)
        {
            await db.Listings
                .Where(l => l.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ViewCount, l => l.ViewCount + 1));
        }

        return MapToDto(listing);
    }

    // ── CREATE ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Kreira listing. Korisnik mora imati ProviderProfile.
    /// Validira PriceMode logiku.
    /// </summary>
    public async Task<(ListingDto? Result, string? Error)> CreateAsync(
        string userId, CreateListingDto dto)
    {
        // Proveravamo da korisnik ima provajder profil
        var provider = await db.ProviderProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (provider is null)
            return (null, "Moraš imati kreiran Provider profil da bi objavljivao listinge.");

        // Validacija kategorije
        var category = await db.Categories.FindAsync(dto.CategoryId);
        if (category is null)
            return (null, $"Kategorija sa Id={dto.CategoryId} ne postoji.");

        // Validacija cene
        var priceError = ValidatePrice(dto.PriceMode, dto.FixedPrice, dto.PriceFrom, dto.PriceTo);
        if (priceError is not null) return (null, priceError);

        var listing = new Listing
        {
            ProviderProfileId = provider.Id,
            CategoryId        = dto.CategoryId,
            Title             = dto.Title.Trim(),
            Description       = dto.Description.Trim(),
            Location          = dto.Location.Trim(),
            PriceMode         = dto.PriceMode,
            FixedPrice        = dto.FixedPrice,
            PriceFrom         = dto.PriceFrom,
            PriceTo           = dto.PriceTo,
            Status            = ListingStatus.Active,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
        };

        db.Listings.Add(listing);

        // Uvećajmo TotalListings na provajder profilu
        provider.TotalListings++;

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Listing kreiran: {Id} od provajdera {ProviderId}", listing.Id, provider.Id);

        // Učitaj navigation properties za DTO
        listing.Category        = category;
        listing.ProviderProfile = provider;

        var user = await userManager.FindByIdAsync(userId);
        listing.ProviderProfile.User = user!;

        return (MapToDto(listing), null);
    }

    // ── UPDATE ─────────────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateAsync(
        int listingId, string userId, UpdateListingDto dto)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo izmene.");

        var category = await db.Categories.FindAsync(dto.CategoryId);
        if (category is null)
            return (false, $"Kategorija sa Id={dto.CategoryId} ne postoji.");

        var priceError = ValidatePrice(dto.PriceMode, dto.FixedPrice, dto.PriceFrom, dto.PriceTo);
        if (priceError is not null) return (false, priceError);

        listing.Title       = dto.Title.Trim();
        listing.Description = dto.Description.Trim();
        listing.Location    = dto.Location.Trim();
        listing.CategoryId  = dto.CategoryId;
        listing.PriceMode   = dto.PriceMode;
        listing.FixedPrice  = dto.FixedPrice;
        listing.PriceFrom   = dto.PriceFrom;
        listing.PriceTo     = dto.PriceTo;
        listing.UpdatedAt   = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── UPDATE STATUS ──────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateStatusAsync(
        int listingId, string userId, string statusStr)
    {
        if (!Enum.TryParse<ListingStatus>(statusStr, ignoreCase: true, out var newStatus))
            return (false, $"Nepoznat status '{statusStr}'. Dozvoljeni: Active, Paused, Archived.");

        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo izmene.");

        listing.Status    = newStatus;
        listing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── DELETE (arhiviranje) ───────────────────────────────────────────────
    /// <summary>
    /// "Brisanje" = postavljanje Status = Archived.
    /// Fizičko brisanje se ne radi — čuvamo istoriju.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteAsync(int listingId, string userId)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo brisanja.");

        listing.Status    = ListingStatus.Archived;
        listing.UpdatedAt = DateTime.UtcNow;

        // Smanji TotalListings na provider profilu
        var provider = await db.ProviderProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);
        if (provider is not null && provider.TotalListings > 0)
            provider.TotalListings--;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── UPLOAD IMAGE ───────────────────────────────────────────────────────
    /// <summary>
    /// Dodaje sliku na listing. Max 5 slika po listingu.
    /// Fajl se čuva u wwwroot/uploads/listings/{listingId}/.
    /// </summary>
    public async Task<(ListingImageDto? Result, string? Error)> UploadImageAsync(
        int listingId, string userId, IFormFile file)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (null, "Listing nije pronađen ili nemate pravo izmene.");

        // Učitaj slike
        var existingImages = await db.ListingImages
            .Where(i => i.ListingId == listingId)
            .ToListAsync();

        if (existingImages.Count >= 5)
            return (null, "Listing može imati maksimalno 5 slika.");

        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowed.Contains(file.ContentType))
            return (null, "Dozvoljeni formati: JPEG, PNG, WebP.");

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (file.Length > maxBytes)
            return (null, "Slika ne sme biti veća od 10 MB.");

        // Putanja: wwwroot/uploads/listings/{listingId}/{guid}.{ext}
        var ext       = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName  = $"{Guid.NewGuid():N}{ext}";
        var uploadDir = Path.Combine(env.WebRootPath, "uploads", "listings", listingId.ToString());
        Directory.CreateDirectory(uploadDir);

        var filePath = Path.Combine(uploadDir, fileName);
        await using (var stream = File.Create(filePath))
            await file.CopyToAsync(stream);

        // Relativna putanja — pun URL sastavlja MediaUrlJsonModifier pri
        // serijalizaciji, pa promena domena ne kvari postojeće slike.
        var imageUrl = $"/uploads/listings/{listingId}/{fileName}";

        var sortOrder = existingImages.Count > 0
            ? existingImages.Max(i => i.SortOrder) + 1
            : 0;

        var image = new ListingImage
        {
            ListingId = listingId,
            ImageUrl  = imageUrl,
            SortOrder = sortOrder
        };

        db.ListingImages.Add(image);
        listing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return (new ListingImageDto
        {
            Id        = image.Id,
            ImageUrl  = image.ImageUrl,
            SortOrder = image.SortOrder
        }, null);
    }

    // ── DELETE IMAGE ───────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> DeleteImageAsync(
        int listingId, int imageId, string userId)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo izmene.");

        var image = await db.ListingImages
            .FirstOrDefaultAsync(i => i.Id == imageId && i.ListingId == listingId);

        if (image is null)
            return (false, "Slika nije pronađena.");

        // Pokušaj brisanje fajla sa diska
        try
        {
            var localPath = Path.Combine(
                env.WebRootPath, "uploads", "listings",
                listingId.ToString(),
                Path.GetFileName(image.ImageUrl));
            if (File.Exists(localPath)) File.Delete(localPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nije moguće obrisati fajl slike {ImageId}", imageId);
        }

        db.ListingImages.Remove(image);
        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── GET BY PROVIDER ────────────────────────────────────────────────────
    public async Task<List<ListingDto>> GetByProviderAsync(string userId)
    {
        return await db.Listings
            .AsNoTracking()
            .Include(l => l.Category)
            .Include(l => l.Images.OrderBy(i => i.SortOrder))
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .Where(l => l.ProviderProfile.UserId == userId &&
                        l.Status != ListingStatus.Archived)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => MapToDto(l))
            .ToListAsync();
    }

    // ── HELPERS ────────────────────────────────────────────────────────────

    /// <summary>Vraća listing samo ako mu je userId vlasnik.</summary>
    private async Task<Listing?> GetOwnedListingAsync(int listingId, string userId)
    {
        return await db.Listings
            .Include(l => l.ProviderProfile)
            .FirstOrDefaultAsync(l =>
                l.Id == listingId &&
                l.ProviderProfile.UserId == userId &&
                l.Status != ListingStatus.Archived);
    }

    private static string? ValidatePrice(
        PriceMode mode, decimal? fixed_, decimal? from, decimal? to)
    {
        return mode switch
        {
            PriceMode.Fixed      when fixed_ is null or <= 0
                => "Za fiksu cenu, unesi pozitivan iznos u polju fixedPrice.",
            PriceMode.Range      when from is null || to is null || from <= 0 || to <= 0
                => "Za cenovni raspon, unesi priceFrom i priceTo (oba pozitivna).",
            PriceMode.Range      when from >= to
                => "priceFrom mora biti manji od priceTo.",
            _   => null
        };
    }

    private static ListingDto MapToDto(Listing l) => new()
    {
        Id           = l.Id,
        Title        = l.Title,
        Description  = l.Description,
        Location     = l.Location,
        PriceMode    = l.PriceMode,
        FixedPrice   = l.FixedPrice,
        PriceFrom    = l.PriceFrom,
        PriceTo      = l.PriceTo,
        Status       = l.Status,
        ViewCount    = l.ViewCount,
        IsBoosted    = l.IsBoosted,
        CreatedAt    = l.CreatedAt,
        UpdatedAt    = l.UpdatedAt,
        CategoryId   = l.CategoryId,
        CategoryName = l.Category?.Name  ?? string.Empty,
        CategorySlug = l.Category?.Slug  ?? string.Empty,
        Images       = l.Images?
            .OrderBy(i => i.SortOrder)
            .Select(i => new ListingImageDto
            {
                Id        = i.Id,
                ImageUrl  = i.ImageUrl,
                SortOrder = i.SortOrder
            })
            .ToList() ?? [],
        Provider = l.ProviderProfile is null ? null! : new ProviderSummaryDto
        {
            ProviderProfileId = l.ProviderProfile.Id,
            UserId            = l.ProviderProfile.UserId,
            FullName          = l.ProviderProfile.User?.FullName ?? string.Empty,
            Profession        = l.ProviderProfile.Profession,
            ProfileImageUrl   = l.ProviderProfile.User?.ProfileImageUrl,
            AverageRating     = l.ProviderProfile.AverageRating,
            TotalReviews      = l.ProviderProfile.TotalReviews,
            IsVerified        = l.ProviderProfile.IsVerified
        }
    };
}
