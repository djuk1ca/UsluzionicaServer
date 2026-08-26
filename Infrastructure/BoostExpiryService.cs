using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Background servis koji svakih sat vremena pronalazi i deaktivira istekle boostove.
///
/// Logika pri isticanju boosta:
///   listing.BoostScore -= boost.TokensSpent / boost.DurationDays
///   boost.IsActive = false
///
/// Ako listing više nema ni jednog aktivnog boosta:
///   listing.IsBoosted = false
///   listing.BoostScore = 0      (cleanup floating point ostataka)
///   listing.BoostExpiresAt = null
///
/// Koristi IServiceScopeFactory jer je BackgroundService singleton
/// a AppDbContext je scoped — ne smeju se mešati lifetime-ovi.
/// </summary>
public sealed class BoostExpiryService(
    IServiceScopeFactory scopeFactory,
    DistributedLock      locks,
    ILogger<BoostExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("BoostExpiryService pokrenut.");

        // Odmah pri startu (u slučaju da je server bio dugo ugašen)
        await ExpireBoostsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Čekaj sat vremena do sledećeg ciklusa
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // Aplikacija se gasi
            }

            await ExpireBoostsAsync(stoppingToken);
        }

        logger.LogInformation("BoostExpiryService zaustavljen.");
    }

    private async Task ExpireBoostsAsync(CancellationToken ct)
    {
        try
        {
            // Tačno jedna instanca sme da izvrši ovaj posao.
            // Bez ovoga bi se sa dve instance API-ja svaki oglas dvaput izgubio isti BoostScore.
            // TTL od 5 minuta je duži od trajanja posla, a kraći od intervala
            // ponavljanja — ako instanca pukne, lock se sam oslobodi.
            await using var lease = await locks.TryAcquireAsync("boost-expiry", TimeSpan.FromMinutes(5));
            if (lease is null) return;

            await using var scope               = scopeFactory.CreateAsyncScope();
            var db                              = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService             = scope.ServiceProvider.GetRequiredService<NotificationService>();
            var now = DateTime.UtcNow;

            // Učitaj istekle aktivne boostove sa navigacijom ka Listing-u
            var expiredBoosts = await db.ListingBoosts
                .Include(b => b.Listing)
                .Where(b => b.IsActive && b.ExpiresAt <= now)
                .ToListAsync(ct);

            if (expiredBoosts.Count == 0)
            {
                logger.LogDebug("BoostExpiry: nema isteklih boostova.");
                return;
            }

            // Pratimo koji listinzi su pogođeni
            var affectedListingIds = new HashSet<int>();

            foreach (var boost in expiredBoosts)
            {
                // Delta koja se oduzima — ista vrednost koja je dodata pri kreiranju boosta
                var delta = Math.Round(boost.TokensSpent / boost.DurationDays, 4);

                boost.IsActive            = false;
                boost.Listing.BoostScore  = Math.Max(0m, boost.Listing.BoostScore - delta);
                affectedListingIds.Add(boost.ListingId);
            }

            await db.SaveChangesAsync(ct);

            // Za svaki pogođeni listing: provjeri da li ima još aktivnih boostova
            foreach (var listingId in affectedListingIds)
            {
                var listing = await db.Listings.FindAsync([listingId], ct);
                if (listing is null) continue;

                var hasActive = await db.ListingBoosts.AnyAsync(
                    b => b.ListingId == listingId && b.IsActive, ct);

                if (!hasActive)
                {
                    // Nema više aktivnih boostova — ugasi boost flag
                    listing.IsBoosted      = false;
                    listing.BoostScore     = 0m;     // cleanup potencijalnih floating point ostataka
                    listing.BoostExpiresAt = null;
                }
                else
                {
                    // Još ima aktivnih boostova — ažuriraj BoostExpiresAt na najkasnije
                    listing.BoostExpiresAt = await db.ListingBoosts
                        .Where(b => b.ListingId == listingId && b.IsActive)
                        .MaxAsync(b => (DateTime?)b.ExpiresAt, ct);
                }
            }

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "BoostExpiry: deaktivirano {Count} boost(ova), pogođeno {Listings} listing(a).",
                expiredBoosts.Count, affectedListingIds.Count);

            // ── 24h upozorenje za boostove koji uskoro ističu ─────────────────
            // Prozor: ističu između now+23h i now+25h (±1h tolerancija zbog sat-časovnog ciklusa)
            var warnFrom = now.AddHours(23);
            var warnTo   = now.AddHours(25);

            var expiringBoosts = await db.ListingBoosts
                .Include(b => b.Listing)
                    .ThenInclude(l => l.ProviderProfile)
                .Where(b => b.IsActive && b.ExpiresAt >= warnFrom && b.ExpiresAt <= warnTo)
                .ToListAsync(ct);

            foreach (var boost in expiringBoosts)
            {
                var providerUserId = boost.Listing.ProviderProfile?.UserId;
                if (providerUserId is null) continue;

                await notificationService.SendAsync(
                    providerUserId,
                    NotificationKind.BoostExpiring,
                    "Boost uskoro ističe!",
                    $"Boost za oglas \"{boost.Listing.Title}\" ističe za manje od 24 sata.",
                    boost.ListingId);
            }

            if (expiringBoosts.Count > 0)
                logger.LogInformation(
                    "BoostExpiry: poslato {Count} upozorenja o bliskom isticanju.",
                    expiringBoosts.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ne ubijamo background service zbog greške —
            // probaće ponovo za sat vremena.
            logger.LogError(ex, "Greška pri isticanju boost-ova.");
        }
    }
}
