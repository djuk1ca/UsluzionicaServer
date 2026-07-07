using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Review modul — pisanje recenzija i kalkulacija proseka providera.
///
/// Pravila:
///   - Jedan autor = jedna recenzija po listingu (UNIQUE u bazi).
///   - BookingRequestId je opciono; ako je dat mora biti Completed i vlasništvo autora.
///   - Nakon svake nove recenzije recalculate ProviderProfile.AverageRating i TotalReviews.
/// </summary>
public sealed class ReviewService(
    AppDbContext            db,
    NotificationService     notificationService,
    ILogger<ReviewService>  logger)
{
    // ── CREATE ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Klijent piše recenziju za listing.
    /// Vraća grešku ako je korisnik već ostavio recenziju za taj listing,
    /// ako pokušava da oceni sopstveni listing, ili ako prosleđeni
    /// BookingRequest nije validan.
    /// </summary>
    public async Task<(ReviewDto?, string?)> CreateAsync(string authorId, CreateReviewDto dto)
    {
        // Listing mora postojati
        var listing = await db.Listings
            .AsNoTracking()
            .Include(l => l.ProviderProfile)
            .FirstOrDefaultAsync(l => l.Id == dto.ListingId);

        if (listing is null)
            return (null, "Listing nije pronađen.");

        // Autor ne može oceniti sopstveni listing
        if (listing.ProviderProfile.UserId == authorId)
            return (null, "Ne možete ostaviti recenziju na sopstvenom oglasu.");

        // Validacija BookingRequestId — opciono ali strogo ako je dato
        if (dto.BookingRequestId.HasValue)
        {
            var booking = await db.BookingRequests
                .AsNoTracking()
                .FirstOrDefaultAsync(b =>
                    b.Id       == dto.BookingRequestId.Value &&
                    b.ClientId == authorId);

            if (booking is null)
                return (null, "Booking zahtev nije pronađen ili ne pripada vam.");

            if (booking.Status != BookingStatus.Completed)
                return (null, "Recenzija vezana za booking može se ostaviti samo za završenu uslugu.");

            if (booking.ListingId != dto.ListingId)
                return (null, "Booking zahtev ne odgovara navedenom listingu.");
        }

        var author = await db.Users.FindAsync(authorId);
        if (author is null)
            return (null, "Korisnik nije pronađen.");

        var now    = DateTime.UtcNow;
        var review = new Review
        {
            ListingId        = dto.ListingId,
            BookingRequestId = dto.BookingRequestId,
            AuthorId         = authorId,
            Stars            = dto.Stars,
            Comment          = dto.Comment?.Trim(),
            CreatedAt        = now
        };

        db.Reviews.Add(review);

        try
        {
            await db.SaveChangesAsync(); // UNIQUE constraint baca ako već postoji
        }
        catch (DbUpdateException)
        {
            return (null, "Već ste ostavili recenziju za ovaj oglas.");
        }

        // Ažuriraj agregirane statistike providera
        await RecalculateProviderRatingAsync(listing.ProviderProfileId);

        await notificationService.SendAsync(
            listing.ProviderProfile.UserId,
            NotificationKind.NewReview,
            "Nova recenzija",
            $"{author.FullName} je ostavio/la {dto.Stars}★ na \"{listing.Title}\".",
            listing.Id);

        logger.LogInformation(
            "Recenzija #{Id} kreirana: autor={AuthorId}, listing={ListingId}, stars={Stars}",
            review.Id, authorId, dto.ListingId, dto.Stars);

        return (MapToDto(review, listing.Title, author), null);
    }

    // ── GET BY LISTING ─────────────────────────────────────────────────────
    /// <summary>
    /// Sve recenzije jednog oglasa, sortirane od najnovije.
    /// Javni endpoint — ne zahteva autentifikaciju.
    /// </summary>
    public async Task<List<ReviewDto>> GetByListingAsync(int listingId, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        return await db.Reviews
            .AsNoTracking()
            .Include(r => r.Author)
            .Include(r => r.Listing)
            .Where(r => r.ListingId == listingId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ReviewDto
            {
                Id               = r.Id,
                ListingId        = r.ListingId,
                ListingTitle     = r.Listing.Title,
                BookingRequestId = r.BookingRequestId,
                AuthorId         = r.AuthorId,
                AuthorName       = r.Author.FullName,
                AuthorImageUrl   = r.Author.ProfileImageUrl,
                Stars            = r.Stars,
                Comment          = r.Comment,
                CreatedAt        = r.CreatedAt
            })
            .ToListAsync();
    }

    // ── GET BY PROVIDER ────────────────────────────────────────────────────
    /// <summary>
    /// Sve recenzije svih oglasa jednog providera, sortirane od najnovije.
    /// Javni endpoint.
    /// </summary>
    public async Task<List<ReviewDto>> GetByProviderAsync(int providerProfileId, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        return await db.Reviews
            .AsNoTracking()
            .Include(r => r.Author)
            .Include(r => r.Listing)
            .Where(r => r.Listing.ProviderProfileId == providerProfileId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ReviewDto
            {
                Id               = r.Id,
                ListingId        = r.ListingId,
                ListingTitle     = r.Listing.Title,
                BookingRequestId = r.BookingRequestId,
                AuthorId         = r.AuthorId,
                AuthorName       = r.Author.FullName,
                AuthorImageUrl   = r.Author.ProfileImageUrl,
                Stars            = r.Stars,
                Comment          = r.Comment,
                CreatedAt        = r.CreatedAt
            })
            .ToListAsync();
    }

    // ── GET SUMMARY ────────────────────────────────────────────────────────
    /// <summary>
    /// Agregirane statistike providera: prosek, ukupan broj, raspored po zvezdicama.
    /// Čita direktno iz ProviderProfile (live kalkulisane vrednosti).
    /// StarBreakdown se računa u memoriji iz Reviews tabele.
    /// </summary>
    public async Task<ReviewSummaryDto?> GetSummaryAsync(int providerProfileId)
    {
        var profile = await db.ProviderProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerProfileId);

        if (profile is null)
            return null;

        // Raspored po zvezdicama — grupisano u SQL
        var breakdown = await db.Reviews
            .AsNoTracking()
            .Where(r => r.Listing.ProviderProfileId == providerProfileId)
            .GroupBy(r => r.Stars)
            .Select(g => new { Stars = g.Key, Count = g.Count() })
            .ToListAsync();

        // Popuni sve zvezdice (1–5) čak i ako nema nijedne recenzije za tu vrednost
        var starBreakdown = Enumerable.Range(1, 5)
            .ToDictionary(
                s => s,
                s => breakdown.FirstOrDefault(b => b.Stars == s)?.Count ?? 0);

        return new ReviewSummaryDto
        {
            ProviderProfileId = providerProfileId,
            AverageRating     = profile.AverageRating,
            TotalReviews      = profile.TotalReviews,
            StarBreakdown     = starBreakdown
        };
    }

    // ── RECALCULATE ────────────────────────────────────────────────────────
    /// <summary>
    /// Recalculate ProviderProfile.AverageRating i TotalReviews
    /// na osnovu svih recenzija svih oglasa tog providera.
    /// Poziva se posle svake nove recenzije.
    /// </summary>
    private async Task RecalculateProviderRatingAsync(int providerProfileId)
    {
        var stats = await db.Reviews
            .Where(r => r.Listing.ProviderProfileId == providerProfileId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Avg   = g.Average(r => (decimal)r.Stars),
                Count = g.Count()
            })
            .FirstOrDefaultAsync();

        var profile = await db.ProviderProfiles.FindAsync(providerProfileId);
        if (profile is null) return;

        profile.AverageRating = stats is not null
            ? Math.Round(stats.Avg, 2)
            : 0m;
        profile.TotalReviews = stats?.Count ?? 0;

        await db.SaveChangesAsync();
    }

    // ── HELPER ─────────────────────────────────────────────────────────────
    private static ReviewDto MapToDto(Review r, string listingTitle, ApplicationUser author) => new()
    {
        Id               = r.Id,
        ListingId        = r.ListingId,
        ListingTitle     = listingTitle,
        BookingRequestId = r.BookingRequestId,
        AuthorId         = r.AuthorId,
        AuthorName       = author.FullName,
        AuthorImageUrl   = author.ProfileImageUrl,
        Stars            = r.Stars,
        Comment          = r.Comment,
        CreatedAt        = r.CreatedAt
    };
}
