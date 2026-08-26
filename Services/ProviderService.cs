using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class ProviderService(
    AppDbContext                 db,
    UserManager<ApplicationUser> userManager,
    IWebHostEnvironment          env,
    IConfiguration               config,
    ReferralService              referralService,
    CacheService                 cache,
    ILogger<ProviderService>     logger)
{
    /// <summary>
    /// Kratak TTL — profil je javan i čita ga svako ko otvori oglas, ali se
    /// menja češće od kategorija (nova recenzija menja AverageRating, nov oglas
    /// menja TotalListings). 5 minuta je kompromis: skida najveći deo čitanja,
    /// a zastarelost je kratka i bezopasna.
    /// </summary>
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromMinutes(5);

    // ── AKTIVACIJA ─────────────────────────────────────────────────────────
    /// <summary>
    /// Aktivira provajderski status korisnika:
    ///   1. Validacije (email, duplikat, kategorije, grad)
    ///   2. Kreira ProviderProfile + ProviderCategory veze
    ///   3. Postavlja User.IsProvider = true
    ///   4. Okida referral nagradu ako postoji Pending referral
    /// </summary>
    public async Task<(ProviderProfileDto? Result, string? Error)> ActivateAsync(
        string userId, ActivateProviderDto dto)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (null, "Korisnik nije pronađen.");

        // Mora imati verifikovan email
        if (!user.EmailConfirmed)
            return (null, "Morate potvrditi email adresu pre aktivacije provajder naloga.");

        // Već je provajder?
        if (await db.ProviderProfiles.AnyAsync(p => p.UserId == userId))
            return (null, "Provajder profil već postoji za ovaj nalog.");

        // Validacija lokacije
        if (!SerbianMunicipalities.All.Contains(dto.Location))
            return (null, $"'{dto.Location}' nije prepoznata opština u Srbiji.");

        // Validacija kategorija — min 1, max 10, sve moraju postojati
        if (dto.CategoryIds.Count > 10)
            return (null, "Možeš odabrati maksimalno 10 kategorija.");

        var distinctCatIds = dto.CategoryIds.Distinct().ToList();
        var existingCatIds = await db.Categories
            .Where(c => distinctCatIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();

        var missingCatIds = distinctCatIds.Except(existingCatIds).ToList();
        if (missingCatIds.Count > 0)
            return (null, $"Kategorije sa Id={string.Join(", ", missingCatIds)} ne postoje.");

        // ── Kreira ProviderProfile ─────────────────────────────────────────
        var profile = new ProviderProfile
        {
            UserId     = userId,
            Profession = dto.Profession.Trim(),
            Bio        = dto.Bio?.Trim(),
            Location   = dto.Location.Trim(),
            Instagram  = dto.Instagram?.Trim(),
            CreatedAt  = DateTime.UtcNow
        };

        db.ProviderProfiles.Add(profile);
        await db.SaveChangesAsync(); // Potrebno da dobijemo profile.Id za ProviderCategory

        // ── Dodaje kategorije ──────────────────────────────────────────────
        var providerCategories = distinctCatIds.Select(cId => new ProviderCategory
        {
            ProviderProfileId = profile.Id,
            CategoryId        = cId
        }).ToList();

        db.ProviderCategories.AddRange(providerCategories);

        // ── IsProvider = true ──────────────────────────────────────────────
        user.IsProvider = true;
        await userManager.UpdateAsync(user);

        // ── Referral nagrada — druga rata ──────────────────────────────────
        // Prva rata je isplaćena kad je ovaj korisnik potvrdio email.
        await referralService.TryRewardActivationAsync(userId);

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Provajder aktiviran: userId={UserId}, profileId={ProfileId}",
            userId, profile.Id);

        // Učitaj za DTO
        return (await GetProfileDtoAsync(profile.Id, includeListings: false), null);
    }

    // ── GET SOPSTVENI PROFIL ───────────────────────────────────────────────
    public async Task<ProviderProfileDto?> GetMyProfileAsync(string userId)
    {
        var profile = await db.ProviderProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId);

        return profile is null ? null : await GetProfileDtoAsync(profile.Id, includeListings: true);
    }

    // ── GET JAVNI PROFIL ───────────────────────────────────────────────────
    public async Task<ProviderProfileDto?> GetPublicProfileAsync(int providerProfileId)
    {
        // KEŠIRANO: javni profil povlači profil + korisnika + kategorije +
        // sve oglase sa slikama - najskuplji čitalački upit u aplikaciji.
        //
        // GetOrSetAsync ne kešira null, pa nepostojeći profil svaki put ide u
        // bazu. To je ovde ispravno: ID-jevi profila su sekvencijalni i mali,
        // pa bi keširanje promašaja bilo lako zloupotrebiti da se keš napuni
        // beskorisnim zapisima.
        var cached = await cache.GetAsync<ProviderProfileDto>(
            CacheService.Keys.ProviderProfile(providerProfileId));
        if (cached is not null) return cached;

        var fresh = await GetProfileDtoAsync(providerProfileId, includeListings: true);
        if (fresh is not null)
            await cache.SetAsync(
                CacheService.Keys.ProviderProfile(providerProfileId), fresh, ProfileTtl);

        return fresh;
    }

    /// <summary>
    /// Brise keširan javni profil. Poziva se pri svakoj izmeni koja se u njemu
    /// vidi — inace bi korisnik izmenio profil i do 5 minuta gledao staro stanje.
    /// </summary>
    private Task InvalidateProfileAsync(int providerProfileId) =>
        cache.RemoveAsync(CacheService.Keys.ProviderProfile(providerProfileId));

    // ── UPDATE ─────────────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateAsync(
        string userId, UpdateProviderDto dto)
    {
        var profile = await db.ProviderProfiles
            .Include(p => p.ProviderCategories)
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile is null)
            return (false, "Provajder profil nije pronađen. Aktiviraj ga prvo.");

        if (!SerbianMunicipalities.All.Contains(dto.Location))
            return (false, $"'{dto.Location}' nije prepoznata opština u Srbiji.");

        if (dto.CategoryIds.Count > 10)
            return (false, "Možeš odabrati maksimalno 10 kategorija.");

        var distinctCatIds = dto.CategoryIds.Distinct().ToList();
        var existingCatIds = await db.Categories
            .Where(c => distinctCatIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync();

        var missing = distinctCatIds.Except(existingCatIds).ToList();
        if (missing.Count > 0)
            return (false, $"Kategorije sa Id={string.Join(", ", missing)} ne postoje.");

        // Ažuriraj osnovne podatke
        profile.Profession = dto.Profession.Trim();
        profile.Bio        = dto.Bio?.Trim();
        profile.Location   = dto.Location.Trim();
        profile.Instagram  = dto.Instagram?.Trim();

        // Zameni kategorije: obriši stare, dodaj nove
        db.ProviderCategories.RemoveRange(profile.ProviderCategories);

        var newCategories = distinctCatIds.Select(cId => new ProviderCategory
        {
            ProviderProfileId = profile.Id,
            CategoryId        = cId
        }).ToList();

        db.ProviderCategories.AddRange(newCategories);

        await db.SaveChangesAsync();
        await InvalidateProfileAsync(profile.Id);

        return (true, null);
    }

    // ── COVER UPLOAD ───────────────────────────────────────────────────────
    public async Task<(string? Url, string? Error)> UploadCoverAsync(
        string userId, IFormFile file)
    {
        var profile = await db.ProviderProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (profile is null)
            return (null, "Provajder profil nije pronađen.");

        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowed.Contains(file.ContentType))
            return (null, "Dozvoljeni formati: JPEG, PNG, WebP.");

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (file.Length > maxBytes)
            return (null, "Slika ne sme biti veća od 10 MB.");

        var ext       = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName  = $"cover_{profile.Id}{ext}";
        var uploadDir = Path.Combine(env.WebRootPath, "uploads", "covers");
        Directory.CreateDirectory(uploadDir);

        var filePath = Path.Combine(uploadDir, fileName);
        await using (var stream = File.Create(filePath))
            await file.CopyToAsync(stream);

        // U bazu ide RELATIVNA putanja; vidi MediaUrls / MediaUrlJsonModifier.
        var relativeUrl = $"/uploads/covers/{fileName}";
        profile.CoverImageUrl = relativeUrl;
        await db.SaveChangesAsync();
        await InvalidateProfileAsync(profile.Id);

        // Vraća se pun URL jer ovaj metod ne prolazi kroz DTO serijalizaciju.
        var absoluteUrl = MediaUrls.ToAbsolute(relativeUrl, config["App:BaseUrl"] ?? string.Empty)!;
        return (absoluteUrl, null);
    }

    // ── LISTINZI PROVAJDERA (javno) ────────────────────────────────────────
    public async Task<List<ListingDto>> GetProviderListingsAsync(int providerProfileId)
    {
        var listings = await db.Listings
            .AsNoTracking()
            .Include(l => l.Category)
            .Include(l => l.Images.OrderBy(i => i.SortOrder))
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .Where(l => l.ProviderProfileId == providerProfileId &&
                        l.Status == ListingStatus.Active)
            .OrderByDescending(l => l.IsBoosted)
            .ThenByDescending(l => l.CreatedAt)
            .ToListAsync();

        return listings.Select(l => MapListingToDto(l)).ToList();
    }

    // ── RECENZIJE PROVAJDERA (javno) ───────────────────────────────────────
    public async Task<List<ReviewSummaryDto>> GetProviderReviewsAsync(
        int providerProfileId, int page = 1, int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page     = Math.Max(page, 1);

        // Reviews su vezani za ProviderProfile putem shadow FK (ProviderProfileId u bazi)
        var reviews = await db.Reviews
            .AsNoTracking()
            .Include(r => r.Author)
            .Include(r => r.Listing)
            .Where(r => EF.Property<int?>(r, "ProviderProfileId") == providerProfileId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return reviews.Select(r => new ReviewSummaryDto
        {
            Id             = r.Id,
            AuthorName     = r.Author.FullName,
            AuthorImageUrl = r.Author.ProfileImageUrl,
            Stars          = r.Stars,
            Comment        = r.Comment,
            ListingTitle   = r.Listing.Title,
            CreatedAt      = r.CreatedAt
        }).ToList();
    }

    // ── SHARED GET PROFILE DTO ─────────────────────────────────────────────
    private async Task<ProviderProfileDto?> GetProfileDtoAsync(
        int providerProfileId, bool includeListings)
    {
        var profile = await db.ProviderProfiles
            .AsNoTracking()
            .Include(p => p.User)
            .Include(p => p.ProviderCategories)
                .ThenInclude(pc => pc.Category)
            .FirstOrDefaultAsync(p => p.Id == providerProfileId);

        if (profile is null) return null;

        var dto = new ProviderProfileDto
        {
            ProviderProfileId = profile.Id,
            UserId            = profile.UserId,
            FullName          = profile.User.FullName,
            ProfileImageUrl   = profile.User.ProfileImageUrl,
            CoverImageUrl     = profile.CoverImageUrl,
            Profession        = profile.Profession,
            Bio               = profile.Bio,
            Location          = profile.Location,
            Instagram         = profile.Instagram,
            AverageRating     = profile.AverageRating,
            TotalReviews      = profile.TotalReviews,
            TotalListings     = profile.TotalListings,
            IsVerified        = profile.IsVerified,
            CreatedAt         = profile.CreatedAt,
            Categories        = profile.ProviderCategories
                .Select(pc => new ProviderCategoryDto
                {
                    CategoryId   = pc.CategoryId,
                    CategoryName = pc.Category.Name,
                    CategorySlug = pc.Category.Slug
                })
                .ToList()
        };

        if (includeListings)
            dto.Listings = await GetProviderListingsAsync(providerProfileId);

        return dto;
    }

    // ── LISTING MAPPER (kopija iz ListingService) ──────────────────────────
    private static ListingDto MapListingToDto(Listing l) => new()
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
