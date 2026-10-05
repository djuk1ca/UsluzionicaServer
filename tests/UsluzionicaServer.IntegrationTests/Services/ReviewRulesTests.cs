using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo „ocena samo uz izvršenu uslugu".
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
    public async Task UzIzvrsenuUslugu_Prolazi_IUlaziUProsek()
    {
        // Pozitivna kontrola: bez nje bi i kod koji odbija SVE ocene prošao.
        var (klijentId, majstorId, oglasId, profilId) = await PripremiAsync();
        var bookingId = await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        var (ocena, greska) = await OceniAsync(klijentId, oglasId, bookingId, zvezdice: 4);

        greska.Should().BeNull();
        ocena.Should().NotBeNull();

        var profil = await Query(db => db.ProviderProfiles.SingleAsync(p => p.Id == profilId));
        profil.TotalReviews.Should().Be(1);
        profil.AverageRating.Should().Be(4m);
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

        var profil = await Query(db => db.ProviderProfiles.SingleAsync(p => p.Id == profilId));
        profil.AverageRating.Should().Be(5m);
    }

    [Fact]
    public async Task PodsetnikZaOcenu_StaraOcenaGaNeGasi()
    {
        var (klijentId, majstorId, oglasId, _) = await PripremiAsync();
        await UbaciStaruOcenuAsync(klijentId, oglasId, zvezdice: 3);
        await IzvrsenaUslugaAsync(klijentId, majstorId, oglasId);

        var podsetnici = await WithService<BookingService, List<PendingReviewDto>>(
            svc => svc.GetPendingReviewsAsync(klijentId));

        podsetnici.Should().ContainSingle("stara ocena se ne vidi, pa klijent treba da dobije poziv da je ponovi");
    }

    /// <summary>Ocena kakva je mogla nastati pre pravila — bez usluge, direktno u bazi.</summary>
    private Task UbaciStaruOcenuAsync(string autorId, int oglasId, int zvezdice) =>
        Query(async db =>
        {
            db.Reviews.Add(new Review
            {
                ListingId = oglasId,
                AuthorId  = autorId,
                Stars     = zvezdice,
                Comment   = "Pre pravila"
            });
            return await db.SaveChangesAsync();
        });
}
