using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;
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

        var greska = await NaplatiAsync(userId, dto.TokensToSpend, TokenKind.BoostSpend,
            $"Boost \"{listing.Title}\" — {dto.DurationDays} dana (+{boostDelta:0.####} BoostScore)",
            listingId, now);
        if (greska is not null)
        {
            await trx.RollbackAsync();
            return (false, greska);
        }

        // ListingBoost zapis
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

        // Ažuriraj listing (aditivan BoostScore)
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

    /// <summary>
    /// Autor ističe svoj posao — iste cene i trajanja kao za oglase.
    ///
    /// Posao nema red po isticanju sa sopstvenim krajem kao oglas: novo
    /// isticanje dok staro traje dodaje na BoostScore i produžava kraj, a sve
    /// se gasi zajedno. Razlog je pauza: dok posao ima izvršioca, ostatak
    /// isticanja čeka (Job.BoostRemainingOnAssign), pa jedan kraj može da se
    /// pomeri, a skup krajeva po isticanjima ne bi mogao.
    /// </summary>
    public async Task<(bool, string?)> BoostJobAsync(int jobId, string userId, BoostJobDto dto)
    {
        if (!ValidDurations.Contains(dto.DurationDays))
            return (false, $"Trajanje isticanja mora biti 3, 7 ili 14 dana. Prosleđeno: {dto.DurationDays}.");

        if (dto.TokensToSpend <= 0)
            return (false, "Broj tokena mora biti pozitivan.");

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.Status == JobStatus.Open);
        if (job is null || job.AuthorId != userId)
            return (false, "Posao nije pronađen ili nije aktivan.");

        var now   = DateTime.UtcNow;
        var delta = Math.Round(dto.TokensToSpend / dto.DurationDays, 4);
        var traje = job.IsBoosted && job.BoostExpiresAt > now;

        await using var trx = await db.Database.BeginTransactionAsync();

        var greska = await NaplatiAsync(userId, dto.TokensToSpend, TokenKind.JobBoostSpend,
            $"Isticanje posla \"{job.Title}\" — {dto.DurationDays} dana", jobId, now);
        if (greska is not null)
        {
            await trx.RollbackAsync();
            return (false, greska);
        }

        db.JobBoosts.Add(new JobBoost
        {
            JobId        = jobId,
            UserId       = userId,
            TokensSpent  = dto.TokensToSpend,
            DurationDays = dto.DurationDays,
            StartsAt     = now,
            ExpiresAt    = now.AddDays(dto.DurationDays)
        });

        // Uslovno — posao je mogao da dobije izvršioca ili istekne između
        // čitanja i ovde; tada se sve vraća, tokeni ostaju korisniku.
        var kraj = traje ? job.BoostExpiresAt!.Value.AddDays(dto.DurationDays) : now.AddDays(dto.DurationDays);
        var skor = traje ? job.BoostScore + delta : delta;
        var promenjeno = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.IsBoosted, true)
                .SetProperty(j => j.BoostScore, skor)
                .SetProperty(j => j.BoostExpiresAt, kraj)
                .SetProperty(j => j.UpdatedAt, now));
        if (promenjeno == 0)
        {
            await trx.RollbackAsync();
            return (false, "Posao više nije aktivan.");
        }

        await db.SaveChangesAsync();
        await trx.CommitAsync();

        logger.LogInformation(
            "Posao #{Id} istaknut: {Tokens} tokena, {Days} dana (do {Kraj:yyyy-MM-dd HH:mm} UTC).",
            jobId, dto.TokensToSpend, dto.DurationDays, kraj);

        return (true, null);
    }

    /// <summary>
    /// Skida tokene i upisuje stavku u ledger. Vraća poruku greške kad nema
    /// dovoljno tokena. Mora se zvati unutar transakcije pozivaoca.
    ///
    /// Provera i oduzimanje idu u JEDNOJ SQL naredbi:
    ///   UPDATE AspNetUsers SET TokenBalance = TokenBalance - @x
    ///   WHERE Id = @id AND TokenBalance >= @x
    /// Ranije je ovde bilo: pročitaj balans → uporedi u memoriji → oduzmi, pa
    /// su dva paralelna zahteva prolazila istu proveru i balans je odlazio u
    /// minus (TokenConcurrencyTests: 8 zahteva sa balansom za jedan boost —
    /// svih 8 je prošlo). Baza drži ekskluzivnu bravu na redu, pa drugi zahtev
    /// čeka i tek potom ponovo proverava uslov — nad već umanjenim balansom.
    /// </summary>
    private async Task<string?> NaplatiAsync(
        string userId, decimal iznos, TokenKind kind, string opis, int referenceId, DateTime sada)
    {
        var affected = await db.Users
            .Where(u => u.Id == userId && u.TokenBalance >= iznos)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenBalance, u => u.TokenBalance - iznos));

        // ExecuteUpdateAsync zaobilazi change tracker, pa je eventualna praćena
        // instanca korisnika sada zastarela — balans se čita ponovo iz baze.
        var balans = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.TokenBalance)
            .FirstOrDefaultAsync();

        if (affected == 0)
            return $"Nedovoljno tokena. Vaš balans: {balans:0.##}, potrebno: {iznos:0.##}.";

        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = userId,
            Amount       = -iznos,
            Kind         = kind,
            Description  = opis,
            ReferenceId  = referenceId,
            BalanceAfter = balans,
            CreatedAt    = sada
        });

        return null;
    }
}
