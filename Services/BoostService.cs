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

        var now        = DateTime.UtcNow;
        var expiresAt  = now.AddDays(dto.DurationDays);

        // BoostScore delta: tokeni / dani
        var boostDelta = Math.Round(dto.TokensToSpend / dto.DurationDays, 4);

        // ── Transakcija oko celog troška ───────────────────────────────────
        // Bez nje bi pad procesa između skidanja tokena i upisa ListingBoost-a
        // ostavio korisnika bez tokena i bez boosta.
        await using var trx = await db.Database.BeginTransactionAsync();

        // 1. Dedukuj tokene — ATOMIČNO.
        //
        // Ranije je ovde bilo: pročitaj balans → uporedi u memoriji → oduzmi.
        // Između čitanja i upisa postoji prozor u kojem drugi zahtev pročita
        // isti balans, pa oba prođu proveru i balans ode u minus.
        // (Test TokenConcurrencyTests je to dokazao: 8 paralelnih zahteva sa
        //  balansom za jedan boost — svih 8 je prošlo.)
        //
        // Sada provera i oduzimanje idu u JEDNOJ SQL naredbi:
        //   UPDATE AspNetUsers SET TokenBalance = TokenBalance - @x
        //   WHERE Id = @id AND TokenBalance >= @x
        // Baza drži ekskluzivnu bravu na redu, pa drugi zahtev čeka i tek
        // potom ponovo proverava uslov — nad već umanjenim balansom.
        var affected = await db.Users
            .Where(u => u.Id == userId && u.TokenBalance >= dto.TokensToSpend)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.TokenBalance,
                u => u.TokenBalance - dto.TokensToSpend));

        if (affected == 0)
        {
            // Nula pogođenih redova znači da uslov TokenBalance >= iznos nije
            // zadovoljen. (Da korisnik ne postoji, ranija provera vlasništva
            // listinga bi već pukla.)
            await trx.RollbackAsync();

            var trenutni = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.TokenBalance)
                .FirstOrDefaultAsync();

            return (false, $"Nedovoljno tokena. Vaš balans: {trenutni:0.##}, " +
                           $"potrebno: {dto.TokensToSpend:0.##}.");
        }

        // ExecuteUpdateAsync zaobilazi change tracker, pa je eventualna praćena
        // instanca korisnika sada zastarela. Balans čitamo ponovo iz baze da bi
        // BalanceAfter u ledgeru bio tačan.
        var balansPosle = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.TokenBalance)
            .FirstAsync();

        // 2. TokenTransaction (audit)
        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = userId,
            Amount       = -dto.TokensToSpend,
            Kind         = TokenKind.BoostSpend,
            Description  = $"Boost \"{listing.Title}\" — {dto.DurationDays} dana " +
                           $"(+{boostDelta:0.####} BoostScore)",
            ReferenceId  = listingId,
            BalanceAfter = balansPosle,
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

        // Tek sada je trošak konačan — skidanje tokena, ledger zapis, boost i
        // izmena listinga su ili svi upisani ili nijedan.
        await trx.CommitAsync();

        logger.LogInformation(
            "Listing #{Id} boost: +{Delta} BoostScore, novi total: {Total}, " +
            "troši: {Tokens} tokena, traje: {Days} dana (do {Expires:yyyy-MM-dd HH:mm} UTC)",
            listingId, boostDelta, listing.BoostScore,
            dto.TokensToSpend, dto.DurationDays, expiresAt);

        return (true, null);
    }
}
