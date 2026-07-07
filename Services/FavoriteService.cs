using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Favorites;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Omiljeni oglasi i uslugodavaci.
///
/// Toggle pristup: isti endpoint dodaje ako ne postoji, briše ako već postoji.
/// UNIQUE constraint (UserId, ListingId) i (UserId, ProviderProfileId) garantuje
/// da nema duplikata čak i pri paralelnim zahtevima.
/// </summary>
public sealed class FavoriteService(
    AppDbContext             db,
    ILogger<FavoriteService> logger)
{
    // ── TOGGLE LISTING ─────────────────────────────────────────────────────
    /// <summary>
    /// Dodaje oglas u omiljene ako nije tamo, uklanja ga ako već jeste.
    /// Vraća novi status: isFavorited = true/false.
    /// </summary>
    public async Task<(FavoriteStatusDto?, string?)> ToggleListingAsync(string userId, int listingId)
    {
        // Listing mora postojati
        var listingExists = await db.Listings.AnyAsync(l => l.Id == listingId);
        if (!listingExists)
            return (null, "Oglas nije pronađen.");

        var existing = await db.FavoriteListings
            .FirstOrDefaultAsync(f => f.UserId == userId && f.ListingId == listingId);

        if (existing is not null)
        {
            // Već omiljeno — ukloni
            db.FavoriteListings.Remove(existing);
            await db.SaveChangesAsync();
            logger.LogDebug("Korisnik {UserId} uklonio listing {ListingId} iz omiljenih", userId, listingId);
            return (new FavoriteStatusDto { IsFavorited = false }, null);
        }

        // Nije omiljeno — dodaj
        db.FavoriteListings.Add(new FavoriteListing
        {
            UserId    = userId,
            ListingId = listingId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        logger.LogDebug("Korisnik {UserId} dodao listing {ListingId} u omiljene", userId, listingId);
        return (new FavoriteStatusDto { IsFavorited = true }, null);
    }

    // ── TOGGLE PROVIDER ────────────────────────────────────────────────────
    /// <summary>
    /// Dodaje uslugodavca u omiljene ako nije tamo, uklanja ga ako već jeste.
    /// Vraća novi status: isFavorited = true/false.
    /// </summary>
    public async Task<(FavoriteStatusDto?, string?)> ToggleProviderAsync(string userId, int providerProfileId)
    {
        var providerExists = await db.ProviderProfiles.AnyAsync(p => p.Id == providerProfileId);
        if (!providerExists)
            return (null, "Uslugodavac nije pronađen.");

        var existing = await db.FavoriteProviders
            .FirstOrDefaultAsync(f => f.UserId == userId && f.ProviderProfileId == providerProfileId);

        if (existing is not null)
        {
            db.FavoriteProviders.Remove(existing);
            await db.SaveChangesAsync();
            logger.LogDebug("Korisnik {UserId} uklonio providera {ProviderId} iz omiljenih", userId, providerProfileId);
            return (new FavoriteStatusDto { IsFavorited = false }, null);
        }

        db.FavoriteProviders.Add(new FavoriteProvider
        {
            UserId            = userId,
            ProviderProfileId = providerProfileId,
            CreatedAt         = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        logger.LogDebug("Korisnik {UserId} dodao providera {ProviderId} u omiljene", userId, providerProfileId);
        return (new FavoriteStatusDto { IsFavorited = true }, null);
    }

    // ── GET FAVORITE LISTINGS ──────────────────────────────────────────────
    /// <summary>
    /// Lista omiljenih oglasa prijavljenog korisnika, sortirana od najnovije sačuvanog.
    /// </summary>
    public async Task<List<FavoriteListingDto>> GetFavoriteListingsAsync(string userId)
    {
        return await db.FavoriteListings
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FavoriteListingDto
            {
                FavoriteId   = f.Id,
                ListingId    = f.ListingId,
                Title        = f.Listing.Title,
                Location     = f.Listing.Location,
                CategoryName = f.Listing.Category.Name,
                CategorySlug = f.Listing.Category.Slug,
                PriceMode    = f.Listing.PriceMode.ToString(),
                FixedPrice   = f.Listing.FixedPrice,
                PriceFrom    = f.Listing.PriceFrom,
                PriceTo      = f.Listing.PriceTo,
                ThumbnailUrl = f.Listing.Images
                                 .OrderBy(i => i.SortOrder)
                                 .Select(i => i.ImageUrl)
                                 .FirstOrDefault(),
                ProviderName = f.Listing.ProviderProfile.User.FullName,
                IsBoosted    = f.Listing.IsBoosted,
                SavedAt      = f.CreatedAt
            })
            .ToListAsync();
    }

    // ── GET FAVORITE PROVIDERS ─────────────────────────────────────────────
    /// <summary>
    /// Lista omiljenih uslugodavaca prijavljenog korisnika, sortirana od najnovije sačuvanog.
    /// </summary>
    public async Task<List<FavoriteProviderDto>> GetFavoriteProvidersAsync(string userId)
    {
        return await db.FavoriteProviders
            .AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FavoriteProviderDto
            {
                FavoriteId        = f.Id,
                ProviderProfileId = f.ProviderProfileId,
                UserId            = f.ProviderProfile.UserId,
                FullName          = f.ProviderProfile.User.FullName,
                Profession        = f.ProviderProfile.Profession,
                Location          = f.ProviderProfile.Location,
                ProfileImageUrl   = f.ProviderProfile.User.ProfileImageUrl,
                AverageRating     = f.ProviderProfile.AverageRating,
                TotalReviews      = f.ProviderProfile.TotalReviews,
                TotalListings     = f.ProviderProfile.TotalListings,
                IsVerified        = f.ProviderProfile.IsVerified,
                SavedAt           = f.CreatedAt
            })
            .ToListAsync();
    }

    // ── CHECK STATUS ───────────────────────────────────────────────────────
    /// <summary>
    /// Proverava da li je korisnik označio oglas kao omiljeni.
    /// Korisno za inicijalni render srca na UI.
    /// </summary>
    public async Task<bool> IsListingFavoritedAsync(string userId, int listingId) =>
        await db.FavoriteListings
            .AnyAsync(f => f.UserId == userId && f.ListingId == listingId);

    /// <summary>
    /// Proverava da li je korisnik označio uslugodavca kao omiljenog.
    /// </summary>
    public async Task<bool> IsProviderFavoritedAsync(string userId, int providerProfileId) =>
        await db.FavoriteProviders
            .AnyAsync(f => f.UserId == userId && f.ProviderProfileId == providerProfileId);
}
