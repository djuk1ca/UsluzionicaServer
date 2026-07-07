using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Tokens;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Boost modul — provider troši tokene da podigne vidljivost svog listinga.
///
/// BoostScore formula: tokensToSpend / durationDays
/// Primer: 3 tokena × 3 dana → BoostScore += 1.0
///         7 tokena × 7 dana → BoostScore += 1.0
///         6 tokena × 3 dana → BoostScore += 2.0
///
/// BoostScore je ADITIVAN — više istovremenih boostova se sabiraju.
/// BoostExpiryService svakih sat vremena oduzima BoostScore isteklih boostova.
/// </summary>
public sealed class BoostService(
    AppDbContext db,
    ILogger<BoostService> logger)
{
    private static readonly int[] ValidDurations = [3, 7, 14];

    /// <summary>
    /// Provider boostuje listing trošeći tokene.
    ///
    /// Efekti:
    ///   - user.TokenBalance -= tokensToSpend
    ///   - TokenTransaction (Kind=BoostSpend, Amount=-tokens)
    ///   - ListingBoost INSERT
    ///   - listing.BoostScore += tokensToSpend / durationDays
    ///   - listing.IsBoosted = true
    ///   - listing.BoostExpiresAt = max(current, newExpiry)
    /// </summary>
    public async Task<(bool, string?)> BoostListingAsync(
        int listingId, string userId, BoostListingDto dto)
    {
        // Validacija trajanja
        if (!ValidDurations.Contains(dto.DurationDays))
            return (false, $"Trajanje boosta mora biti 3, 7 ili 14 dana. " +
                           $"Prosleđeno: {dto.DurationDays}.");

        // Validacija iznosa
        if (dto.TokensToSpend <= 0)
            return (false, "Broj tokena mora biti pozitivan.");

        // Listing mora biti aktivan i vlasništvo korisnika
        var listing = await db.Listings
            .Include(l => l.ProviderProfile)
            .FirstOrDefaultAsync(l => l.Id == listingId && l.Status == ListingStatus.Active);

        if (listing is null)
            return (false, "Listing nije pronađen ili nije aktivan.");

        if (listing.ProviderProfile.UserId != userId)
            return (false, "Možete boostovati samo sopstvene listinge.");

        // Provera balansa
        var user = await db.Users.FindAsync(userId);
        if (user is null) return (false, "Korisnik nije pronađen.");

        if (user.TokenBalance < dto.TokensToSpend)
            return (false, $"Nedovoljno tokena. Vaš balans: {user.TokenBalance:0.##}, " +
                           $"potrebno: {dto.TokensToSpend:0.##}.");

        var now        = DateTime.UtcNow;
        var expiresAt  = now.AddDays(dto.DurationDays);

        // BoostScore delta: tokeni / dani
        var boostDelta = Math.Round(dto.TokensToSpend / dto.DurationDays, 4);

        // 1. Dedukuj tokene
        user.TokenBalance -= dto.TokensToSpend;

        // 2. TokenTransaction (audit)
        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = userId,
            Amount       = -dto.TokensToSpend,
            Kind         = TokenKind.BoostSpend,
            Description  = $"Boost \"{listing.Title}\" — {dto.DurationDays} dana " +
                           $"(+{boostDelta:0.####} BoostScore)",
            ReferenceId  = listingId,
            BalanceAfter = user.TokenBalance,
            CreatedAt    = now
        });

        // 3. ListingBoost zapis
        db.ListingBoosts.Add(new ListingBoost
        {
            ListingId    = listingId,
            UserId       = userId,
            TokensSpent  = dto.TokensToSpend,
            DurationDays = dto.DurationDays,
            StartsAt     = now,
            ExpiresAt    = expiresAt,
            IsActive     = true
        });

        // 4. Ažuriraj listing (aditivan BoostScore)
        listing.IsBoosted   = true;
        listing.BoostScore += boostDelta;

        // BoostExpiresAt = najkasniji od svih aktivnih boostova
        listing.BoostExpiresAt = listing.BoostExpiresAt.HasValue && listing.BoostExpiresAt > expiresAt
            ? listing.BoostExpiresAt
            : expiresAt;

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Listing #{Id} boost: +{Delta} BoostScore, novi total: {Total}, " +
            "troši: {Tokens} tokena, traje: {Days} dana (do {Expires:yyyy-MM-dd HH:mm} UTC)",
            listingId, boostDelta, listing.BoostScore,
            dto.TokensToSpend, dto.DurationDays, expiresAt);

        return (true, null);
    }
}
