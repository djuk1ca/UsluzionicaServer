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
///   - OCENA SAMO UZ IZVRŠENU USLUGU: BookingRequestId je obavezan, booking mora
///     biti Completed, autor mora biti klijent tog booking-a.
///   - Nakon svake nove recenzije recalculate ProviderProfile.AverageRating i TotalReviews.
///
/// ZAŠTO JE BOOKING OBAVEZAN
///
/// Ranije je bio opcion, pa je ocenu mogao da ostavi svako prijavljen —
/// prijatelj uslugodavca ili konkurent. Ocene su jedina stvar koja Uslužionicu
/// razlikuje od oglasa po Facebook grupama; ocena kojoj se ne može verovati to
/// poništava. Sajt i marketing smeju da tvrde da su ocene potvrđene tek kad
/// ovo pravilo postoji (Marketing 14 P0-2).
///
/// OCENE NASTALE PRE OVOG PRAVILA
///
/// Ne brišu se — samo se ne prikazuju i ne ulaze u prosek (<see cref="Potvrdjene"/>).
/// Odluka je reverzibilna jednim filterom, a autor ništa ne gubi: kad završi
/// uslugu kod istog uslugodavca, stara ocena se nadograđuje u potvrđenu.
/// </summary>
public sealed class ReviewService(
    AppDbContext            db,
    NotificationService     notificationService,
    BlockService            blockService,
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

        // Blokada zatvara i pisanje recenzije.
        //
        // Bez ove provere recenzija bi ostala jedini kanal kojim blokirani i
        // dalje dopire do onoga ko ga je blokirao — i to javno, na njegovom
        // oglasu, sa ocenom koja mu obara prosek.
        if (await blockService.JeBlokiranoAsync(authorId, listing.ProviderProfile.UserId))
            return (null, "Recenzija za ovaj oglas nije moguća.");

        // Ocena samo uz izvršenu uslugu — vidi komentar uz klasu.
        if (dto.BookingRequestId is not int bookingId)
            return (null, "Ocenu možeš ostaviti posle izvršene usluge, iz ekrana Moje rezervacije.");

        var booking = await db.BookingRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.ClientId == authorId);

        if (booking is null)
            return (null, "Rezervacija nije pronađena ili ne pripada vama.");

        if (booking.Status != BookingStatus.Completed)
            return (null, "Ocenu možeš ostaviti tek kad je usluga izvršena.");

        if (booking.ListingId != dto.ListingId)
            return (null, "Rezervacija ne odgovara ovom oglasu.");

        var author = await db.Users.FindAsync(authorId);
        if (author is null)
            return (null, "Korisnik nije pronađen.");

        var now = DateTime.UtcNow;

        // Ocena istog autora na istom oglasu iz vremena pre ovog pravila (bez
        // usluge) se NADOGRAĐUJE. UNIQUE (oglas, autor) bi inače zauvek
        // sprečio potvrđenu ocenu klijentu koji je pre pravila ocenio „na reč".
        var stara = await db.Reviews
            .FirstOrDefaultAsync(r => r.ListingId == dto.ListingId && r.AuthorId == authorId);

        Review review;

        if (stara is { BookingRequestId: null })
        {
            stara.BookingRequestId = bookingId;
            stara.Stars            = dto.Stars;
            stara.Comment          = dto.Comment?.Trim();
            stara.CreatedAt        = now;
            review                 = stara;
        }
        else if (stara is not null)
        {
            return (null, "Već ste ostavili recenziju za ovaj oglas.");
        }
        else
        {
            review = new Review
            {
                ListingId        = dto.ListingId,
                BookingRequestId = bookingId,
                AuthorId         = authorId,
                Stars            = dto.Stars,
                Comment          = dto.Comment?.Trim(),
                CreatedAt        = now
            };

            db.Reviews.Add(review);
        }

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
    public async Task<List<ReviewDto>> GetByListingAsync(
        int listingId, int page, int pageSize, string? viewerUserId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = Potvrdjene(db.Reviews
            .AsNoTracking()
            .Include(r => r.Author)
            .Include(r => r.Listing))
            .Where(r => r.ListingId == listingId);

        // Recenzije blokiranih se sklanjaju gledaocu.
        //
        // Filtrira se po AUTORU recenzije, ne po vlasniku oglasa: sadržaj koji
        // je ovde sporan napisao je autor. Vlasnika pokriva provera nad samim
        // oglasom, koja se dešava pre nego što se do recenzija uopšte stigne.
        if (!string.IsNullOrEmpty(viewerUserId))
        {
            query = query.Where(r => !db.UserBlocks.Any(ub =>
                (ub.BlockerId == viewerUserId && ub.BlockedId == r.AuthorId) ||
                (ub.BlockedId == viewerUserId && ub.BlockerId == r.AuthorId)));
        }

        return await query
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
    public async Task<List<ReviewDto>> GetByProviderAsync(
        int providerProfileId, int page, int pageSize, string? viewerUserId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = Potvrdjene(db.Reviews
            .AsNoTracking()
            .Include(r => r.Author)
            .Include(r => r.Listing))
            .Where(r => r.Listing.ProviderProfileId == providerProfileId);

        // Isto pravilo kao u GetByListingAsync — filtrira se po autoru recenzije.
        if (!string.IsNullOrEmpty(viewerUserId))
        {
            query = query.Where(r => !db.UserBlocks.Any(ub =>
                (ub.BlockerId == viewerUserId && ub.BlockedId == r.AuthorId) ||
                (ub.BlockedId == viewerUserId && ub.BlockerId == r.AuthorId)));
        }

        return await query
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
        var breakdown = await Potvrdjene(db.Reviews.AsNoTracking())
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
        var stats = await Potvrdjene(db.Reviews)
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

    /// <summary>
    /// Samo ocene vezane za izvršenu uslugu. JEDNO mesto za pravilo — prikaz
    /// ocena, raspored zvezdica i prosek moraju brojati isto, inače bi profil
    /// pokazivao prosek od ocena koje se na njemu ne vide.
    /// </summary>
    public static IQueryable<Review> Potvrdjene(IQueryable<Review> ocene) =>
        ocene.Where(r => r.BookingRequestId != null);

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
