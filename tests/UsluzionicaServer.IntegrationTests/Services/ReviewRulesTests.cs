using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo „ocena samo uz izvršenu uslugu" i obostrane ocene: obe
/// strane ocenjuju jednom, a ocena je skrivena dok i druga ne oceni ili ne
/// prođe rok od 14 dana.
///
/// Sajt i marketing tvrde da su ocene potvrđene (Marketing 14 P0-2). Ta tvrdnja
/// je tačna samo dok ovi testovi prolaze — pukne li pravilo, sajt laže, a to je
/// obmanjujuća tvrdnja prema potrošačima, ne samo bag.
/// </summary>
public class ReviewRulesTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<(string KlijentId, string MajstorId, int OglasId, int ProfilId)> PripremiAsync()
    {
        var (majstor, profilId) = await Data.CreateProviderAsync("majstor@test.rs");
        var oglasId             = await Data.CreateActiveListingAsync(majstor.Id);
        var klijent             = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        return (klijent.Id, majstor.Id, oglasId, profilId);
    }

    /// <summary>Pravi tok: zahtev → potvrda → (3 dana) → izvršenje.</summary>
    private async Task<int> IzvrsenaUslugaAsync(string klijentId, string majstorId, int oglasId)
    {
        var (booking, greska) = await WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateBookingDto { ListingId = oglasId }));
        booking.Should().NotBeNull(greska);

        await WithService<BookingService, (bool, string?)>(svc => svc.ConfirmAsync(booking!.Id, majstorId));

        await Query(db => db.BookingRequests.Where(b => b.Id == booking!.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.AcceptedAt, DateTime.UtcNow.AddDays(-4))));

        var (izvrsen, g) = await WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.ExecuteAsync(booking!.Id, majstorId));
        izvrsen.Should().NotBeNull(g);

        return booking!.Id;
    }

    private Task<(ReviewDto?, string?)> OceniAsync(string autorId, int oglasId, int? bookingId, int zvezdice = 5) =>
        WithService<ReviewService, (ReviewDto?, string?)>(svc => svc.CreateAsync(autorId, new CreateReviewDto
        {
            ListingId        = oglasId,
            BookingRequestId = bookingId,
            Stars            = zvezdice,
            Comment          = "Test"
        }));

    [Fact]
    public async Task BezUsluge_Odbijena()
    {
        // NAJVAŽNIJI TEST U KLASI — ovo je tačno ono što je ranije prolazilo.
        var (klijentId, _, oglasId, _) = await PripremiAsync();

        var (ocena, greska) = await OceniAsync(klijentId, oglasId, bookingId: null);

        ocena.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task UzIzvrsenuUslugu_Prolazi_IUlaziUProsek_KadObeStraneOcene()
    {
        // Pozitivna kontrola: bez nje bi i kod koji odbija SVE ocene prošao.
        var (klijentId, majstorId, oglasId, profilId) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        var (ocena, greska) = await OceniAsync(klijentId, oglasId, bookingId, zvezdice: 4);

        greska.Should().BeNull();
        ocena!.IsPublished.Should().BeFalse("čeka da i uslugodavac oceni klijenta");
        (await Query(db => db.ProviderProfiles.SingleAsync(p => p.Id == profilId)))
            .TotalReviews.Should().Be(0, "skrivena ocena ne ulazi u prosek");
        (await WithService<ReviewService, List<ReviewDto>>(svc => svc.GetByListingAsync(oglasId, 1, 50)))
            .Should().BeEmpty();

        var (odMajstora, g2) = await OceniAsync(majstorId, oglasId, bookingId, zvezdice: 5);
        g2.Should().BeNull();
        odMajstora!.IsPublished.Should().BeTrue();
        odMajstora.SubjectRole.Should().Be("Client");

        var profil = await Query(db => db.ProviderProfiles.SingleAsync(p => p.Id == profilId));
        profil.TotalReviews.Should().Be(1);
        profil.AverageRating.Should().Be(4m);

        var klijent = await Query(db => db.Users.SingleAsync(u => u.Id == klijentId));
        klijent.ClientReviewCount.Should().Be(1);
        klijent.ClientRating.Should().Be(5m);

        (await WithService<ReviewService, List<ReviewDto>>(svc => svc.GetByListingAsync(oglasId, 1, 50)))
            .Should().ContainSingle("na oglasu su samo ocene uslugodavca, ne i klijenta");
    }

    [Fact]
    public async Task IstaStranaDvaput_Odbijena()
    {
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        await OceniAsync(klijentId, oglasId, bookingId);
        var (druga, greska) = await OceniAsync(klijentId, oglasId, bookingId);

        druga.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task PosleRoka_Odbijena()
    {
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);
        await Query(db => db.Set<ServiceExecution>().Where(e => e.BookingRequestId == bookingId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ExecutedAt, DateTime.UtcNow.AddDays(-15))));

        var (ocena, greska) = await OceniAsync(klijentId, oglasId, bookingId);

        ocena.Should().BeNull();
        greska.Should().Contain("Rok");
    }

    [Fact]
    public async Task JednostranaOcena_SeObjavljujePosleRoka()
    {
        var (klijentId, majstorId, oglasId, profilId) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);
        await OceniAsync(klijentId, oglasId, bookingId, zvezdice: 3);

        // Još u roku — ništa se ne objavljuje.
        (await WithService<ReviewService, int>(svc => svc.ObjaviIstekleAsync())).Should().Be(0);

        await Query(db => db.Reviews.ExecuteUpdateAsync(s => s.SetProperty(r => r.RevealAt, DateTime.UtcNow.AddMinutes(-1))));
        (await WithService<ReviewService, int>(svc => svc.ObjaviIstekleAsync())).Should().Be(1);

        var profil = await Query(db => db.ProviderProfiles.SingleAsync(p => p.Id == profilId));
        profil.TotalReviews.Should().Be(1);
        profil.AverageRating.Should().Be(3m);
    }

    [Fact]
    public async Task UzNezavrsenuUslugu_Odbijena()
    {
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();

        var (booking, _) = await WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateBookingDto { ListingId = oglasId }));
        await WithService<BookingService, (bool, string?)>(svc => svc.ConfirmAsync(booking!.Id, majstorId));

        var (ocena, greska) = await OceniAsync(klijentId, oglasId, booking!.Id);

        ocena.Should().BeNull("potvrđen, ali neizvršen zahtev nije usluga");
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task TudjaUsluga_Odbijena()
    {
        // Prijatelj uslugodavca ne sme da se „prikači" na tuđu izvršenu uslugu.
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);
        var prijatelj = await Data.CreateConfirmedUserAsync("prijatelj@test.rs");

        var (ocena, _) = await OceniAsync(prijatelj.Id, oglasId, bookingId);

        ocena.Should().BeNull();
    }

    [Fact]
    public async Task StaraOcenaBezUsluge_SeNePrikazuje_INeUlaziUProsek()
    {
        // Ocene iz vremena pre pravila se ne brišu, ali se ne vide i ne broje.
        var (klijentId, _, oglasId, profilId) = await PripremiAsync();
        await UbaciStaruOcenuAsync(klijentId, oglasId, zvezdice: 1);

        var vidljive = await WithService<ReviewService, List<ReviewDto>>(
            svc => svc.GetByListingAsync(oglasId, 1, 50));
        var zbir = await WithService<ReviewService, ReviewSummaryDto?>(
            svc => svc.GetSummaryAsync(profilId));

        vidljive.Should().BeEmpty();
        zbir!.StarBreakdown.Values.Sum().Should().Be(0);
        (await Query(db => db.Reviews.CountAsync())).Should().Be(1, "ocena se NE briše");
    }

    [Fact]
    public async Task StaraOcena_PosleIzvrseneUsluge_SeNadogradjuje()
    {
        // Bez nadogradnje bi UNIQUE (oglas, autor) zauvek sprečio potvrđenu
        // ocenu klijentu koji je pre pravila ocenio „na reč".
        var (klijentId, majstorId, oglasId, profilId) = await PripremiAsync();
        await UbaciStaruOcenuAsync(klijentId, oglasId, zvezdice: 1);
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        var (ocena, greska) = await OceniAsync(klijentId, oglasId, bookingId, zvezdice: 5);

        greska.Should().BeNull();
        ocena!.BookingRequestId.Should().Be(bookingId);
        (await Query(db => db.Reviews.CountAsync())).Should().Be(1);

        await OceniAsync(majstorId, oglasId, bookingId);   // objavljuje obe

        var profil = await Query(db => db.ProviderProfiles.SingleAsync(p => p.Id == profilId));
        profil.AverageRating.Should().Be(5m);
    }

    [Fact]
    public async Task PodsetnikZaOcenu_StaraOcenaGaNeGasi()
    {
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();
        await UbaciStaruOcenuAsync(klijentId, oglasId, zvezdice: 3);
        await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        var podsetnici = await WithService<ReviewService, List<PendingReviewDto>>(
            svc => svc.PendingAsync(klijentId));

        podsetnici.Should().ContainSingle("stara ocena se ne vidi, pa klijent treba da dobije poziv da je ponovi");
    }

    [Fact]
    public async Task Podsetnik_ObeStrane_DokNeOcene()
    {
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        var zaMajstora = await WithService<ReviewService, List<PendingReviewDto>>(svc => svc.PendingAsync(majstorId));
        zaMajstora.Should().ContainSingle(p => p.BookingId == bookingId && p.SubjectRole == "Client");

        await OceniAsync(majstorId, oglasId, bookingId);

        (await WithService<ReviewService, List<PendingReviewDto>>(svc => svc.PendingAsync(majstorId)))
            .Should().BeEmpty();
        (await WithService<ReviewService, List<PendingReviewDto>>(svc => svc.PendingAsync(klijentId)))
            .Should().ContainSingle(p => p.OtherSideReviewed);
    }

    /// <summary>Ocena kakva je mogla nastati pre pravila — bez usluge, direktno u bazi.</summary>
    private Task UbaciStaruOcenuAsync(string autorId, int oglasId, int zvezdice) =>
        Query(async db =>
        {
            var oglas = await db.Listings.Where(l => l.Id == oglasId)
                .Select(l => new { l.Title, l.ProviderProfile.UserId }).SingleAsync();
            db.Reviews.Add(new Review
            {
                ListingId     = oglasId,
                AuthorId      = autorId,
                SubjectUserId = oglas.UserId,
                ContextTitle  = oglas.Title,
                Stars         = zvezdice,
                Comment       = "Pre pravila",
                // Kao posle migracije: stara ocena je „objavljena", ali nije potvrđena.
                PublishedAt   = DateTime.UtcNow,
                RevealAt      = DateTime.UtcNow
            });
            return await db.SaveChangesAsync();
        });
}
