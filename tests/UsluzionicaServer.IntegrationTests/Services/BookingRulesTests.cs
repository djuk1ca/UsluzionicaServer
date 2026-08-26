using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravila životnog ciklusa booking zahteva:
///
///     Pending ─┬─→ Confirmed ─┬─→ Completed   (klijent dobija tokene)
///              │              └─→ Rejected
///              ├─→ Rejected
///              └─→ Cancelled
///
/// Suština svih pravila ispod je ista: KO sme da uradi prelaz i IZ KOG stanja.
/// To su dve nezavisne provere i obe se lako izgube pri refaktoru, pa svaka
/// ima svoj test. Da se izgubi provera "ko", klijent bi mogao sam sebi da
/// potvrdi zahtev i pokrene isplatu tokena.
///
/// Poslednja dva testa čuvaju anti-farming pravilo (N dana + idempotentnost) —
/// jedino mesto u sistemu gde se tokeni STVARAJU ni iz čega, pa i jedino gde
/// greška direktno pravi inflaciju.
/// </summary>
public class BookingRulesTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const decimal NagradaZaUslugu = 0.50m;  // Booking:ServiceRewardTokens
    private const int     DanaDoIzvrsenja = 3;      // Booking:ExecuteAfterDays

    /// <summary>Provajder sa oglasom + klijent sa potvrđenim emailom.</summary>
    private async Task<(string KlijentId, string ProvajderId, int ListingId)> PripremiAsync()
    {
        var (provajder, _) = await Data.CreateProviderAsync("provajder@test.rs");
        var listingId      = await Data.CreateActiveListingAsync(provajder.Id);
        var klijent        = await Data.CreateConfirmedUserAsync("klijent@test.rs");

        return (klijent.Id, provajder.Id, listingId);
    }

    private Task<(BookingDto? Booking, string? Greska)> KreirajAsync(string klijentId, int listingId)
        => WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateBookingDto { ListingId = listingId }));

    private Task<(bool Uspeh, string? Greska)> PotvrdiAsync(int id, string provajderId)
        => WithService<BookingService, (bool, string?)>(svc => svc.ConfirmAsync(id, provajderId));

    private Task<(bool Uspeh, string? Greska)> OdbijAsync(int id, string provajderId)
        => WithService<BookingService, (bool, string?)>(svc => svc.RejectAsync(id, provajderId));

    private Task<(bool Uspeh, string? Greska)> OtkaziAsync(int id, string klijentId)
        => WithService<BookingService, (bool, string?)>(svc => svc.CancelAsync(id, klijentId));

    private Task<(BookingDto? Booking, string? Greska)> IzvrsiAsync(int id, string provajderId)
        => WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.ExecuteAsync(id, provajderId));

    /// <summary>
    /// Pomera AcceptedAt u prošlost da bi 3-dnevno pravilo bilo zadovoljeno.
    ///
    /// Zašto ovako a ne čekanjem: pravilo je vezano za stvarno vreme, a test
    /// ne sme trajati tri dana. Alternativa bi bila apstrakcija nad satom
    /// (IClock), ali to je izmena produkcionog koda zarad testa — a ovde je
    /// dovoljno pomeriti podatak.
    /// </summary>
    private Task PomeriPrihvatanjeUProslostAsync(int bookingId, int dana)
        => Query(db => db.BookingRequests
            .Where(b => b.Id == bookingId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                b => b.AcceptedAt, DateTime.UtcNow.AddDays(-dana))));

    // ── 1. Samo-rezervacija odbijena ───────────────────────────────────────

    [Fact]
    public async Task Kreiranje_KadaJeOglasNjegovSopstveni_Odbija()
    {
        // Bez ovoga bi provajder mogao sam sebi da rezerviše uslugu, potvrdi je
        // i izvrši — i tako sam sebi štampa tokene. Cela anti-farming logika
        // pada na ovoj jednoj proveri.

        // Arrange
        var (_, provajderId, listingId) = await PripremiAsync();

        // Act
        var (booking, greska) = await KreirajAsync(provajderId, listingId);

        // Assert
        booking.Should().BeNull();
        greska.Should().Contain("sopstveni");
        (await Query(db => db.BookingRequests.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Kreiranje_KadaJeOglasTudji_Prolazi()
    {
        // POZITIVAN par prethodnom. Bez njega bi i kod koji odbija SVE zahteve
        // prošao test iznad.

        // Arrange
        var (klijentId, _, listingId) = await PripremiAsync();

        // Act
        var (booking, greska) = await KreirajAsync(klijentId, listingId);

        // Assert
        booking.Should().NotBeNull(greska);
        booking!.Status.Should().Be(nameof(BookingStatus.Pending));
        booking.AcceptedAt.Should().BeNull("nov zahtev još nije prihvaćen");
    }

    // ── 2. Duplikat odbijen ────────────────────────────────────────────────

    [Fact]
    public async Task Kreiranje_KadaVecPostojiAktivanZahtevZaIstiOglas_Odbija()
    {
        // Arrange
        var (klijentId, _, listingId) = await PripremiAsync();
        await KreirajAsync(klijentId, listingId);

        // Act
        var (booking, greska) = await KreirajAsync(klijentId, listingId);

        // Assert
        booking.Should().BeNull();
        greska.Should().Contain("Već imate aktivan");
        (await Query(db => db.BookingRequests.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Kreiranje_KadaJePrethodniZahtevZavrsen_DozvoljavaNovi()
    {
        // NEGATIVAN par: pravilo zabranjuje samo AKTIVNE duplikate (Pending ili
        // Confirmed). Otkazan zahtev ne sme trajno blokirati korisnika — inače
        // jedna slučajna rezervacija zauvek zaključa taj oglas za njega.

        // Arrange
        var (klijentId, _, listingId) = await PripremiAsync();
        var (prvi, _) = await KreirajAsync(klijentId, listingId);
        await OtkaziAsync(prvi!.Id, klijentId);

        // Act
        var (drugi, greska) = await KreirajAsync(klijentId, listingId);

        // Assert
        drugi.Should().NotBeNull(greska);
        (await Query(db => db.BookingRequests.CountAsync())).Should().Be(2);
    }

    // ── 3. Nepotvrđen email odbijen ────────────────────────────────────────

    [Fact]
    public async Task Kreiranje_KadaKlijentNijePotvrdioEmail_Odbija()
    {
        // Druga polovina anti-farming zaštite: bez ovoga bi neko mogao da pravi
        // naloge sa izmišljenim adresama i sakuplja tokene za "izvršene" usluge.
        // Potvrda emaila čini svaki lažni nalog skupim.

        // Arrange
        var (provajder, _) = await Data.CreateProviderAsync("provajder@test.rs");
        var listingId      = await Data.CreateActiveListingAsync(provajder.Id);
        var nepotvrdjeni   = await Data.CreateUnconfirmedUserAsync("nepotvrdjen@test.rs");

        // Act
        var (booking, greska) = await KreirajAsync(nepotvrdjeni.Id, listingId);

        // Assert
        booking.Should().BeNull();
        greska.Should().Contain("verifikovana");
        (await Query(db => db.BookingRequests.CountAsync())).Should().Be(0);
    }

    // ── 4. Potvrda: samo provajder, samo iz Pending ────────────────────────

    [Fact]
    public async Task Potvrda_KadaJePozivaocProvajderIZahtevJePending_Prolazi()
    {
        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);

        // Act
        var (uspeh, greska) = await PotvrdiAsync(booking!.Id, provajderId);

        // Assert
        uspeh.Should().BeTrue(greska);

        var iz = await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id));
        iz.Status.Should().Be(BookingStatus.Confirmed);

        // AcceptedAt pokreće 3-dnevni tajmer — bez njega izvršenje nikad ne bi
        // moglo da se odobri.
        iz.AcceptedAt.Should().NotBeNull();
        iz.AcceptedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Potvrda_KadaJePozivaocKlijent_Odbija()
    {
        // Klijent koji sam sebi potvrđuje zahtev zaobilazi celu saglasnost
        // provajdera i otvara put ka izvršenju i tokenima.

        // Arrange
        var (klijentId, _, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);

        // Act
        var (uspeh, greska) = await PotvrdiAsync(booking!.Id, klijentId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije pronađen");

        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Pending);
    }

    [Fact]
    public async Task Potvrda_KadaZahtevNijeUStatusuPending_Odbija()
    {
        // Druga provera istog metoda — "iz kog stanja". Otkazan zahtev ne sme
        // biti oživljen potvrdom.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await OtkaziAsync(booking!.Id, klijentId);

        // Act
        var (uspeh, greska) = await PotvrdiAsync(booking.Id, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije u statusu čekanja");

        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Cancelled);
    }

    // ── 5. Otkazivanje: samo klijent, samo iz Pending ──────────────────────

    [Fact]
    public async Task Otkazivanje_KadaJePozivaocKlijentIZahtevJePending_Prolazi()
    {
        // Arrange
        var (klijentId, _, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);

        // Act
        var (uspeh, greska) = await OtkaziAsync(booking!.Id, klijentId);

        // Assert
        uspeh.Should().BeTrue(greska);
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Cancelled);
    }

    [Fact]
    public async Task Otkazivanje_KadaJePozivaocProvajder_Odbija()
    {
        // Provajder ima svoj put (Reject). Da može i da otkazuje, u podacima se
        // ne bi videla razlika između "klijent se predomislio" i "provajder je
        // odbio posao" — a to su različite stvari za statistiku i reputaciju.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);

        // Act
        var (uspeh, greska) = await OtkaziAsync(booking!.Id, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije pronađen");
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Pending);
    }

    [Fact]
    public async Task Otkazivanje_KadaJeZahtevVecPotvrdjen_Odbija()
    {
        // Posle potvrde je provajder već rezervisao vreme, pa jednostrano
        // otkazivanje više nije dozvoljeno.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await PotvrdiAsync(booking!.Id, provajderId);

        // Act
        var (uspeh, greska) = await OtkaziAsync(booking.Id, klijentId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("samo dok je u statusu čekanja");
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Confirmed);
    }

    // ── 6. Odbijanje: iz Pending ILI Confirmed ─────────────────────────────

    [Theory]
    [InlineData(false)]  // odbijanje iz Pending
    [InlineData(true)]   // odbijanje iz Confirmed
    public async Task Odbijanje_IzPendingIliConfirmed_Prolazi(bool prvoPotvrdi)
    {
        // Provajder sme da se predomisli i POSLE prihvatanja — sve dok usluga
        // nije izvršena. Ovo je namerno šire od otkazivanja.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);

        if (prvoPotvrdi)
            await PotvrdiAsync(booking!.Id, provajderId);

        // Act
        var (uspeh, greska) = await OdbijAsync(booking!.Id, provajderId);

        // Assert
        uspeh.Should().BeTrue(greska);
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Rejected);
    }

    [Fact]
    public async Task Odbijanje_KadaJeZahtevVecZavrsen_Odbija()
    {
        // NEGATIVAN par prethodnom: "Pending ili Confirmed" znači i da završena
        // stanja NISU dozvoljena. Odbijanje već otkazanog zahteva bi prepisalo
        // istoriju.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await OtkaziAsync(booking!.Id, klijentId);

        // Act
        var (uspeh, greska) = await OdbijAsync(booking.Id, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("ne može odbiti");
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Cancelled);
    }

    // ── 7. Izvršenje tek posle N dana ──────────────────────────────────────

    [Fact]
    public async Task Izvrsenje_PreIstekaTriDana_OdbijaINeIsplacujeTokene()
    {
        // Anti-farming: bez čekanja bi dva naloga u dogovoru mogla da otvore i
        // "izvrše" stotine rezervacija u minuti i naprave tokene iz vazduha.
        // Tri dana ne sprečavaju prevaru, ali je čine presporom da se isplati.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await PotvrdiAsync(booking!.Id, provajderId);

        // Act — AcceptedAt je upravo sada, dakle prošlo je 0 dana
        var (rezultat, greska) = await IzvrsiAsync(booking.Id, provajderId);

        // Assert
        rezultat.Should().BeNull();
        greska.Should().Contain("tek za");

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(0m);
        (await Query(db => db.ServiceExecutions.CountAsync())).Should().Be(0);
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task Izvrsenje_PosleIstekaTriDana_ProlaziIIsplacujeTokeneKlijentu()
    {
        // POZITIVAN par. Bez njega bi kod koji ODUVEK odbija izvršenje prošao
        // test iznad — a to bi značilo da niko nikad ne može dobiti nagradu.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await PotvrdiAsync(booking!.Id, provajderId);
        await PomeriPrihvatanjeUProslostAsync(booking.Id, DanaDoIzvrsenja + 1);

        // Act
        var (rezultat, greska) = await IzvrsiAsync(booking.Id, provajderId);

        // Assert
        rezultat.Should().NotBeNull(greska);

        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .Status.Should().Be(BookingStatus.Completed);

        (await Query(db => db.ServiceExecutions.CountAsync())).Should().Be(1);

        // Nagradu dobija KLIJENT, ne provajder — provajder je već naplatio uslugu.
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(NagradaZaUslugu);
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(0m);

        var zapis = await Query(db => db.TokenTransactions
            .SingleAsync(t => t.Kind == TokenKind.ServiceReward));
        zapis.UserId.Should().Be(klijentId);
        zapis.Amount.Should().Be(NagradaZaUslugu);
        zapis.BalanceAfter.Should().Be(NagradaZaUslugu);
    }

    [Fact]
    public async Task Izvrsenje_KadaZahtevNijePotvrdjen_Odbija()
    {
        // Izvršenje sme samo iz Confirmed. Da radi i iz Pending, provajder bi
        // mogao da preskoči saglasnost klijenta u potpunosti.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);

        // Act
        var (rezultat, greska) = await IzvrsiAsync(booking!.Id, provajderId);

        // Assert
        rezultat.Should().BeNull();
        greska.Should().Contain("Potvrđeno");
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(0m);
    }

    // ── 8. Ponovljeno izvršenje je idempotentno ────────────────────────────

    [Fact]
    public async Task Izvrsenje_KadaSePozoveDvaput_NeIsplacujeNagraduDvaput()
    {
        // Dvostruki klik na "Izvršeno" ne sme da udvostruči nagradu. Zaštita je
        // provera postojanja ServiceExecution zapisa — a ne status, jer je
        // status u tom trenutku već Completed.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await PotvrdiAsync(booking!.Id, provajderId);
        await PomeriPrihvatanjeUProslostAsync(booking.Id, DanaDoIzvrsenja + 1);

        await IzvrsiAsync(booking.Id, provajderId);

        // Act
        var (rezultat, _) = await IzvrsiAsync(booking.Id, provajderId);

        // Assert
        // Drugi poziv je odbijen zato što status više nije Confirmed — što je
        // ispravno, ali NIJE isti razlog koji pisac koda očekuje. Bitno je da
        // nagrada nije isplaćena dvaput, bez obzira koja provera je zaustavila.
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(NagradaZaUslugu,
            "nagrada se isplaćuje tačno jednom po izvršenoj usluzi");

        (await Query(db => db.ServiceExecutions.CountAsync())).Should().Be(1);
        (await Query(db => db.TokenTransactions.CountAsync(t => t.Kind == TokenKind.ServiceReward)))
            .Should().Be(1);

        rezultat.Should().BeNull("drugi poziv ne sme prijaviti novo izvršenje");
    }

    [Fact]
    public async Task Izvrsenje_KadaPozivaocNijeProvajder_Odbija()
    {
        // Klijent ne sme sam sebi da potvrdi da je usluga izvršena — to bi bio
        // najkraći put do besplatnih tokena.

        // Arrange
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var (booking, _) = await KreirajAsync(klijentId, listingId);
        await PotvrdiAsync(booking!.Id, provajderId);
        await PomeriPrihvatanjeUProslostAsync(booking.Id, DanaDoIzvrsenja + 1);

        // Act
        var (rezultat, greska) = await IzvrsiAsync(booking.Id, klijentId);

        // Assert
        rezultat.Should().BeNull();
        greska.Should().Contain("nije pronađen");
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(0m);
        (await Query(db => db.ServiceExecutions.CountAsync())).Should().Be(0);
    }
}
