using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.Services;

/// <summary>
/// Review modul — obostrane ocene posle izvršene usluge ili izvršenog posla,
/// i prosek ocenjenog u ulozi u kojoj je ocenjen.
///
/// Ko koga ocenjuje (smer je u podacima — <see cref="Review.SubjectRole"/>):
///   usluga: klijent → uslugodavac (Provider), uslugodavac → klijent (Client)
///   posao:  autor → izvršilac (Provider),     izvršilac → autor (Client)
///
/// Pravila:
///   - OCENA SAMO UZ IZVRŠENU USLUGU ILI POSAO, samo strane u njemu, jednom
///     po strani (UNIQUE u bazi), u roku od 14 dana od izvršenja.
///   - SKRIVENA DOK I DRUGA STRANA NE OCENI ili dok rok ne prođe — tada se
///     objavljuju obe. Inače bi ko ocenjuje drugi mogao da vrati „za kaznu".
///   - Prosek se računa samo iz objavljenih potvrđenih ocena (<see cref="Vidljive"/>).
///
/// ZAŠTO JE IZVRŠENJE OBAVEZNO
///
/// Ranije je booking bio opcion, pa je ocenu mogao da ostavi svako prijavljen —
/// prijatelj uslugodavca ili konkurent. Ocene su jedina stvar koja Uslužionicu
/// razlikuje od oglasa po Facebook grupama; ocena kojoj se ne može verovati to
/// poništava. Sajt i marketing smeju da tvrde da su ocene potvrđene tek kad
/// ovo pravilo postoji (Marketing 14 P0-2).
///
/// OCENE NASTALE PRE OVOG PRAVILA
///
/// Ne brišu se — samo se ne prikazuju i ne ulaze u prosek (IsVerified = false).
/// Odluka je reverzibilna jednim filterom, a autor ništa ne gubi: kad završi
/// uslugu kod istog uslugodavca, stara ocena se nadograđuje u potvrđenu.
/// </summary>
public sealed class ReviewService(
    AppDbContext            db,
    JobPravila              pravila,
    NotificationService     notificationService,
    BlockService            blockService,
    ILogger<ReviewService>  logger)
{
    /// <summary>Šta se ocenjuje — zajedničko za uslugu i posao.</summary>
    private sealed record Kontekst(
        string     SubjectId,
        ReviewRole Uloga,
        string     Naslov,
        int?       ListingId,
        int?       BookingId,
        int?       JobId,
        DateTime   Izvrseno);

    // ── CREATE ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Strana u izvršenoj usluzi (<c>BookingRequestId</c>) ili izvršenom poslu
    /// (<c>JobId</c>) ocenjuje drugu stranu.
    /// </summary>
    public async Task<(ReviewDto?, string?)> CreateAsync(string authorId, CreateReviewDto dto)
    {
        if ((dto.BookingRequestId is null) == (dto.JobId is null))
            return (null, dto.JobId is null && dto.BookingRequestId is null
                ? "Ocenu možeš ostaviti posle izvršene usluge, iz ekrana Moje rezervacije."
                : "Ocena ide uz uslugu ili uz posao, ne oba.");

        var (kontekst, greska) = dto.BookingRequestId is int bookingId
            ? await UslugaAsync(authorId, bookingId, dto.ListingId)
            : await PosaoAsync(authorId, dto.JobId!.Value);
        if (kontekst is null) return (null, greska);

        var now = DateTime.UtcNow;
        var rok = kontekst.Izvrseno.AddDays(pravila.RokZaOcene);
        if (now > rok)
            return (null, $"Rok za ocenu je {pravila.RokZaOcene} dana od izvršenja i prošao je.");

        // Blokada zatvara i pisanje ocene — inače bi ocena ostala jedini kanal
        // kojim blokirani dopire do onoga ko ga je blokirao, i to javno.
        if (await blockService.JeBlokiranoAsync(authorId, kontekst.SubjectId))
            return (null, "Ocena ovog korisnika nije moguća.");

        var author = await db.Users.FindAsync(authorId);
        if (author is null)
            return (null, "Korisnik nije pronađen.");

        // Druga strana je već ocenila → objavljuju se obe, odmah.
        var druga = await db.Reviews.FirstOrDefaultAsync(r =>
            r.AuthorId == kontekst.SubjectId &&
            (kontekst.BookingId != null ? r.BookingRequestId == kontekst.BookingId
                                        : r.JobId == kontekst.JobId));

        // Ocena istog klijenta na istom oglasu iz vremena pre pravila (bez
        // usluge) se NADOGRAĐUJE — klijent koji je pre pravila ocenio „na reč"
        // ne gubi ništa kad završi uslugu.
        var stara = kontekst is { Uloga: ReviewRole.Provider, ListingId: not null, BookingId: not null }
            ? await db.Reviews.FirstOrDefaultAsync(r =>
                  r.ListingId == kontekst.ListingId && r.AuthorId == authorId && !r.IsVerified)
            : null;

        var review = stara ?? new Review { AuthorId = authorId };
        review.ListingId        = kontekst.ListingId;
        review.BookingRequestId = kontekst.BookingId;
        review.JobId            = kontekst.JobId;
        review.SubjectUserId    = kontekst.SubjectId;
        review.SubjectRole      = kontekst.Uloga;
        review.ContextTitle     = kontekst.Naslov;
        review.Stars            = dto.Stars;
        review.Comment          = string.IsNullOrWhiteSpace(dto.Comment) ? null : dto.Comment.Trim();
        review.CreatedAt        = now;
        review.IsVerified       = true;
        review.RevealAt         = rok;
        review.PublishedAt      = druga is not null ? now : null;
        if (stara is null) db.Reviews.Add(review);

        if (druga is { PublishedAt: null })
            druga.PublishedAt = now;

        try
        {
            await db.SaveChangesAsync(); // UNIQUE (autor, usluga/posao) baca ako već postoji
        }
        catch (DbUpdateException)
        {
            return (null, "Već si ocenio ovu saradnju.");
        }

        if (druga is not null)
        {
            await PreracunajAsync(kontekst.SubjectId, kontekst.Uloga);
            await PreracunajAsync(authorId, druga.SubjectRole);

            await ObavestiOObjaviAsync(review, author.FullName);
            await ObavestiOObjaviAsync(druga, (await db.Users.AsNoTracking()
                .Where(u => u.Id == druga.AuthorId).Select(u => u.FullName).FirstAsync()));
        }
        else
        {
            await notificationService.SendAsync(
                kontekst.SubjectId,
                NotificationKind.ReviewRequested,
                "Oceni i ti",
                $"{author.FullName} te je ocenio za „{kontekst.Naslov}“. Oceni i ti — ocene se objavljuju zajedno.",
                // Samo ID posla: aplikacija po njemu otvara ocenu posla. ID
                // zahteva bi bio dvosmislen (isti broj može biti i posao), a
                // ocenu usluge ionako nudi podsetnik pri otvaranju aplikacije.
                kontekst.JobId);
        }

        logger.LogInformation(
            "Ocena #{Id}: autor={AuthorId}, ocenjeni={SubjectId} ({Role}), {Stars}★, objavljena={Objavljena}",
            review.Id, authorId, kontekst.SubjectId, kontekst.Uloga, dto.Stars, review.PublishedAt is not null);

        return (MapToDto(review, author), null);
    }

    private async Task<(Kontekst?, string?)> UslugaAsync(string authorId, int bookingId, int? listingId)
    {
        var b = await db.BookingRequests
            .AsNoTracking()
            .Where(b => b.Id == bookingId && (b.ClientId == authorId || b.ProviderUserId == authorId))
            .Select(b => new
            {
                b.ListingId,
                b.ClientId,
                b.ProviderUserId,
                b.Status,
                b.UpdatedAt,
                Naslov   = b.Listing.Title,
                Izvrseno = b.ServiceExecution != null ? (DateTime?)b.ServiceExecution.ExecutedAt : null
            })
            .FirstOrDefaultAsync();

        if (b is null)
            return (null, "Rezervacija nije pronađena ili ne pripada vama.");

        // Uslugodavac ne ocenjuje klijenta koji je otkazao — ni klijent
        // uslugodavca: ocena ide samo uz izvršenu uslugu (odluka 7).
        if (b.Status != BookingStatus.Completed)
            return (null, "Ocenu možeš ostaviti tek kad je usluga izvršena.");

        if (listingId is int l && l != b.ListingId)
            return (null, "Rezervacija ne odgovara ovom oglasu.");

        var klijent = b.ClientId == authorId;
        return (new Kontekst(
            SubjectId: klijent ? b.ProviderUserId : b.ClientId,
            Uloga:     klijent ? ReviewRole.Provider : ReviewRole.Client,
            Naslov:    b.Naslov,
            ListingId: b.ListingId,
            BookingId: bookingId,
            JobId:     null,
            Izvrseno:  b.Izvrseno ?? b.UpdatedAt), null);
    }

    private async Task<(Kontekst?, string?)> PosaoAsync(string authorId, int jobId)
    {
        var j = await db.Jobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new
            {
                j.AuthorId,
                j.Title,
                j.Status,
                j.CompletedAt,
                IzvrsilacId = db.JobApplications
                    .Where(a => a.Id == j.AssignedApplicationId)
                    .Select(a => a.ProviderUserId)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync();

        if (j is null || (j.AuthorId != authorId && j.IzvrsilacId != authorId))
            return (null, "Posao nije pronađen.");

        if (j.Status != JobStatus.Completed || j.CompletedAt is not DateTime izvrseno || j.IzvrsilacId is null)
            return (null, "Ocenu možeš ostaviti tek kad je posao izvršen.");

        var autor = j.AuthorId == authorId;
        return (new Kontekst(
            SubjectId: autor ? j.IzvrsilacId : j.AuthorId,
            Uloga:     autor ? ReviewRole.Provider : ReviewRole.Client,
            Naslov:    j.Title,
            ListingId: null,
            BookingId: null,
            JobId:     jobId,
            Izvrseno:  izvrseno), null);
    }

    // ── OBJAVA POSLE ROKA ──────────────────────────────────────────────────
    /// <summary>
    /// Ocene kojima je prošao rok, a druga strana nije ocenila, postaju
    /// vidljive. Poziva ga <see cref="Infrastructure.ReviewRevealService"/>.
    /// </summary>
    public async Task<int> ObjaviIstekleAsync(CancellationToken ct = default)
    {
        var sada = DateTime.UtcNow;
        var zaObjavu = await db.Reviews
            .Where(r => r.PublishedAt == null && r.RevealAt <= sada)
            .Include(r => r.Author)
            .ToListAsync(ct);

        foreach (var r in zaObjavu)
            r.PublishedAt = sada;
        await db.SaveChangesAsync(ct);

        foreach (var (subjekat, uloga) in zaObjavu.Select(r => (r.SubjectUserId, r.SubjectRole)).Distinct())
            await PreracunajAsync(subjekat, uloga);

        foreach (var r in zaObjavu)
            await ObavestiOObjaviAsync(r, r.Author.FullName);

        return zaObjavu.Count;
    }

    private Task ObavestiOObjaviAsync(Review r, string autorIme) =>
        notificationService.SendAsync(
            r.SubjectUserId,
            NotificationKind.NewReview,
            "Nova ocena",
            $"{autorIme} te ocenjuje za „{r.ContextTitle}“ sa {r.Stars}★.",
            // Ocenu oglasa aplikacija otvara na oglasu; ostale na listi ocena.
            r.SubjectRole == ReviewRole.Provider ? r.ListingId : null);

    // ── ČEKA OCENU ─────────────────────────────────────────────────────────
    /// <summary>
    /// Izvršene usluge i poslovi u roku koje korisnik još nije ocenio — za
    /// podsetnik pri otvaranju aplikacije i „Čeka ocenu" u Mojim poslovima.
    /// </summary>
    public async Task<List<PendingReviewDto>> PendingAsync(string userId)
    {
        var od = DateTime.UtcNow.AddDays(-pravila.RokZaOcene);

        var usluge = await db.BookingRequests
            .AsNoTracking()
            .Where(b => (b.ClientId == userId || b.ProviderUserId == userId)
                     && b.Status == BookingStatus.Completed
                     && (b.ServiceExecution != null ? b.ServiceExecution.ExecutedAt : b.UpdatedAt) > od
                     && !db.Reviews.Any(r => r.AuthorId == userId && r.BookingRequestId == b.Id))
            .Select(b => new PendingReviewDto
            {
                Kind         = "Booking",
                BookingId    = b.Id,
                ListingId    = b.ListingId,
                ListingTitle = b.Listing.Title,
                SubjectRole  = b.ClientId == userId ? nameof(ReviewRole.Provider) : nameof(ReviewRole.Client),
                ProviderName = b.ClientId == userId ? b.Provider.FullName : b.Client.FullName,
                CompletedAt  = b.ServiceExecution != null ? b.ServiceExecution.ExecutedAt : b.UpdatedAt,
                OtherSideReviewed = db.Reviews.Any(r => r.BookingRequestId == b.Id && r.AuthorId != userId)
            })
            .ToListAsync();

        var poslovi = await db.Jobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Completed && j.CompletedAt > od
                     && !db.Reviews.Any(r => r.AuthorId == userId && r.JobId == j.Id))
            .Select(j => new
            {
                j.Id,
                j.Title,
                j.AuthorId,
                AutorIme    = j.Author.FullName,
                CompletedAt = j.CompletedAt!.Value,
                Izvrsilac   = db.JobApplications
                    .Where(a => a.Id == j.AssignedApplicationId)
                    .Select(a => new { a.ProviderUserId, a.Provider.FullName })
                    .FirstOrDefault(),
                DrugaOcenila = db.Reviews.Any(r => r.JobId == j.Id && r.AuthorId != userId)
            })
            .Where(j => j.AuthorId == userId || j.Izvrsilac!.ProviderUserId == userId)
            .ToListAsync();

        return [.. usluge.Concat(poslovi.Select(j => new PendingReviewDto
            {
                Kind         = "Job",
                JobId        = j.Id,
                ListingTitle = j.Title,
                SubjectRole  = j.AuthorId == userId ? nameof(ReviewRole.Provider) : nameof(ReviewRole.Client),
                ProviderName = j.AuthorId == userId ? j.Izvrsilac?.FullName ?? "" : j.AutorIme,
                CompletedAt  = j.CompletedAt,
                OtherSideReviewed = j.DrugaOcenila
            }))
            .Select(p => { p.ReviewDeadline = p.CompletedAt.AddDays(pravila.RokZaOcene); return p; })
            .OrderByDescending(p => p.CompletedAt)];
    }

    // ── GET BY LISTING ─────────────────────────────────────────────────────
    /// <summary>
    /// Objavljene ocene uslugodavca uz jedan oglas, od najnovije.
    /// Javni endpoint — ne zahteva autentifikaciju.
    /// </summary>
    public async Task<List<ReviewDto>> GetByListingAsync(
        int listingId, int page, int pageSize, string? viewerUserId = null)
    {
        var query = Vidljive(db.Reviews.AsNoTracking())
            .Where(r => r.ListingId == listingId && r.SubjectRole == ReviewRole.Provider);

        return await StranaAsync(query, page, pageSize, viewerUserId);
    }

    // ── GET BY PROVIDER ────────────────────────────────────────────────────
    /// <summary>
    /// Sve objavljene ocene uslugodavca — sa usluga i sa poslova — od najnovije.
    /// Javni endpoint.
    /// </summary>
    public async Task<List<ReviewDto>> GetByProviderAsync(
        int providerProfileId, int page, int pageSize, string? viewerUserId = null)
    {
        var userId = await db.ProviderProfiles
            .Where(p => p.Id == providerProfileId)
            .Select(p => p.UserId)
            .FirstOrDefaultAsync();
        if (userId is null) return [];

        var query = Vidljive(db.Reviews.AsNoTracking())
            .Where(r => r.SubjectUserId == userId && r.SubjectRole == ReviewRole.Provider);

        return await StranaAsync(query, page, pageSize, viewerUserId);
    }

    // ── KAO NARUČILAC ──────────────────────────────────────────────────────
    /// <summary>
    /// Ocene korisnika kao naručioca (od uslugodavaca i izvršilaca) — za sheet
    /// „★ 4,8 · 6 ocena kao naručilac" na poslu, zahtevu i u četu.
    /// </summary>
    public async Task<ClientReviewsDto?> GetClientReviewsAsync(
        string userId, int page, int pageSize, string? viewerUserId)
    {
        var korisnik = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FullName, u.ClientRating, u.ClientReviewCount })
            .FirstOrDefaultAsync();
        if (korisnik is null) return null;

        var query = Vidljive(db.Reviews.AsNoTracking())
            .Where(r => r.SubjectUserId == userId && r.SubjectRole == ReviewRole.Client);

        return new ClientReviewsDto
        {
            UserId        = userId,
            FullName      = korisnik.FullName,
            AverageRating = korisnik.ClientRating,
            TotalReviews  = korisnik.ClientReviewCount,
            Items         = await StranaAsync(query, page, pageSize, viewerUserId)
        };
    }

    // ── GET SUMMARY ────────────────────────────────────────────────────────
    /// <summary>
    /// Agregirane statistike providera: prosek, ukupan broj, raspored po zvezdicama.
    /// Prosek i broj čita iz ProviderProfile; raspored računa iz istih ocena.
    /// </summary>
    public async Task<ReviewSummaryDto?> GetSummaryAsync(int providerProfileId)
    {
        var profile = await db.ProviderProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == providerProfileId);

        if (profile is null)
            return null;

        var breakdown = await Vidljive(db.Reviews.AsNoTracking())
            .Where(r => r.SubjectUserId == profile.UserId && r.SubjectRole == ReviewRole.Provider)
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
    /// Prosek ocenjenog u jednoj ulozi: uslugodavac → ProviderProfile,
    /// naručilac → ApplicationUser.ClientRating. Poziva se kad god neka ocena
    /// postane vidljiva.
    ///
    /// ExecuteUpdate, ne praćen entitet: korisnik je možda praćen u istom
    /// kontekstu, a UserManager.UpdateAsync bi prepisao i balans tokena (vidi
    /// AppDbContext.ZastitiBalans).
    /// </summary>
    private async Task PreracunajAsync(string subjectId, ReviewRole uloga)
    {
        var stats = await Vidljive(db.Reviews)
            .Where(r => r.SubjectUserId == subjectId && r.SubjectRole == uloga)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Avg   = g.Average(r => (decimal)r.Stars),
                Count = g.Count()
            })
            .FirstOrDefaultAsync();

        var prosek = stats is not null ? Math.Round(stats.Avg, 2) : 0m;
        var broj   = stats?.Count ?? 0;

        if (uloga == ReviewRole.Provider)
            await db.ProviderProfiles
                .Where(p => p.UserId == subjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.AverageRating, prosek)
                    .SetProperty(p => p.TotalReviews,  broj));
        else
            await db.Users
                .Where(u => u.Id == subjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.ClientRating,      prosek)
                    .SetProperty(u => u.ClientReviewCount, broj));
    }

    // ── HELPER ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Potvrđene i objavljene ocene. JEDNO mesto za pravilo — prikaz ocena,
    /// raspored zvezdica i prosek moraju brojati isto, inače bi profil
    /// pokazivao prosek od ocena koje se na njemu ne vide.
    /// </summary>
    public static IQueryable<Review> Vidljive(IQueryable<Review> ocene) =>
        ocene.Where(r => r.IsVerified && r.PublishedAt != null);

    private async Task<List<ReviewDto>> StranaAsync(
        IQueryable<Review> query, int page, int pageSize, string? viewerUserId)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page     = Math.Max(page, 1);

        // Ocene blokiranih se sklanjaju gledaocu — po AUTORU ocene, jer je on
        // napisao sporan sadržaj.
        if (!string.IsNullOrEmpty(viewerUserId))
        {
            query = query.Where(r => !db.UserBlocks.Any(ub =>
                (ub.BlockerId == viewerUserId && ub.BlockedId == r.AuthorId) ||
                (ub.BlockedId == viewerUserId && ub.BlockerId == r.AuthorId)));
        }

        return await query
            .OrderByDescending(r => r.PublishedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ReviewDto
            {
                Id               = r.Id,
                ListingId        = r.ListingId ?? 0,
                ListingTitle     = r.ContextTitle,
                BookingRequestId = r.BookingRequestId,
                JobId            = r.JobId,
                Context          = r.BookingRequestId != null || r.ListingId != null ? "Booking" : "Job",
                SubjectRole      = r.SubjectRole.ToString(),
                AuthorId         = r.AuthorId,
                AuthorName       = r.Author.FullName,
                AuthorImageUrl   = r.Author.ProfileImageUrl,
                Stars            = r.Stars,
                Comment          = r.Comment,
                CreatedAt        = r.CreatedAt
            })
            .ToListAsync();
    }

    private static ReviewDto MapToDto(Review r, ApplicationUser author) => new()
    {
        Id               = r.Id,
        ListingId        = r.ListingId ?? 0,
        ListingTitle     = r.ContextTitle,
        BookingRequestId = r.BookingRequestId,
        JobId            = r.JobId,
        Context          = r.JobId != null ? "Job" : "Booking",
        SubjectRole      = r.SubjectRole.ToString(),
        AuthorId         = r.AuthorId,
        AuthorName       = author.FullName,
        AuthorImageUrl   = author.ProfileImageUrl,
        Stars            = r.Stars,
        Comment          = r.Comment,
        CreatedAt        = r.CreatedAt,
        IsPublished      = r.PublishedAt is not null,
        RevealAt         = r.RevealAt
    };
}
