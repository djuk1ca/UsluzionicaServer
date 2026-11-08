using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Admin skida čekanje do „Izvršeno" potvrđenom terminu (pouzdani korisnici),
/// ili se ono skida samo kad su oba učesnika označena kao pouzdana.
///
/// Uz BookingRulesTests: tamo je čuvano anti-farming pravilo (N dana), ovde
/// jedini legitiman izuzetak od njega. Testovi paze na tri stvari:
///   1. izuzetak važi samo za potvrđen termin i samo kad ga da admin,
///   2. menja se SAMO trenutak od kog je izvršenje dozvoljeno — nagrade i
///      ocene ostaju iste,
///   3. aplikacija (DTO) i podsetnik čitaju isto pravilo kao server.
/// </summary>
public class BookingSkinutoCekanjeTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const decimal NagradaZaUslugu   = 0.50m;  // Booking:ServiceRewardTokens
    private const decimal NagradaProvajderu = 0.20m;  // Booking:ProviderRewardTokens

    private async Task<(string KlijentId, string ProvajderId, int ListingId)> PripremiAsync()
    {
        var (provajder, _) = await Data.CreateProviderAsync("provajder@test.rs");
        var listingId      = await Data.CreateActiveListingAsync(provajder.Id);
        var klijent        = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        return (klijent.Id, provajder.Id, listingId);
    }

    /// <summary>Zahtev → potvrda; čekanje tek počinje.</summary>
    private async Task<int> PotvrdjenAsync(string klijentId, string provajderId, int listingId)
    {
        var (booking, greska) = await WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateBookingDto { ListingId = listingId }));
        booking.Should().NotBeNull(greska);

        var (ok, g) = await WithService<BookingService, (bool, string?)>(
            svc => svc.ConfirmAsync(booking!.Id, provajderId));
        ok.Should().BeTrue(g);
        return booking!.Id;
    }

    private Task<(bool Ok, string? Greska, bool NePostoji)> SkiniAsync(int id, string adminId)
        => WithService<BookingService, (bool, string?, bool)>(svc => svc.SkiniCekanjeAsync(id, adminId));

    private Task<(BookingDto? Booking, string? Greska)> IzvrsiAsync(int id, string provajderId)
        => WithService<BookingService, (BookingDto?, string?)>(svc => svc.ExecuteAsync(id, provajderId));

    private Task<BookingDto> KodUslugodavcaAsync(int id, string provajderId)
        => WithService<BookingService, BookingDto>(
            async svc => (await svc.GetIncomingAsync(provajderId)).Single(b => b.Id == id));

    private Task OznaciPouzdanAsync(string userId)
        => WithService<AdminService, bool>(svc => svc.SetTrustedAsync(userId, true));

    private async Task<HttpClient> PrijavljenAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users  = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var tokeni = scope.ServiceProvider.GetRequiredService<TokenService>();

        var user  = (await users.FindByEmailAsync(email))!;
        var token = tokeni.GenerateAccessToken(user, await users.GetRolesAsync(user));

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── 1. Admin skida čekanje → izvršenje odmah, nagrade i ocene iste ─────

    [Fact]
    public async Task SkinutoCekanje_IzvrsenjeOdmah_NagradeIOceneKaoInace()
    {
        // Arrange
        var admin = await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);

        // Pre skidanja: pravilo važi
        (await IzvrsiAsync(id, provajderId)).Greska.Should().Contain("tek za");

        // Act
        var (ok, greska, _) = await SkiniAsync(id, admin.Id);
        var (rezultat, g)   = await IzvrsiAsync(id, provajderId);

        // Assert — izvršeno, iste nagrade kao posle čekanja
        ok.Should().BeTrue(greska);
        rezultat.Should().NotBeNull(g);
        rezultat!.Status.Should().Be("Completed");

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(NagradaZaUslugu);
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(NagradaProvajderu);

        // Ocene: obe strane mogu odmah, kao posle svakog izvršenja
        var (ocenaKlijenta, g1) = await WithService<ReviewService, (ReviewDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateReviewDto
                { ListingId = listingId, BookingRequestId = id, Stars = 5, Comment = "Odlično" }));
        var (ocenaUslugodavca, g2) = await WithService<ReviewService, (ReviewDto?, string?)>(
            svc => svc.CreateAsync(provajderId, new CreateReviewDto
                { BookingRequestId = id, Stars = 5, Comment = "Korektan" }));
        ocenaKlijenta.Should().NotBeNull(g1);
        ocenaUslugodavca.Should().NotBeNull(g2);
    }

    [Fact]
    public async Task SkinutoCekanje_BeleziKoIKada_IAplikacijaVidiOdmah()
    {
        // Arrange
        var admin = await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);

        var pre = await KodUslugodavcaAsync(id, provajderId);
        pre.CanExecute.Should().BeFalse();
        pre.ExecuteWaitWaived.Should().BeFalse();

        // Act
        await SkiniAsync(id, admin.Id);

        // Assert — revizija
        var red = await Query(db => db.BookingRequests.SingleAsync(b => b.Id == id));
        red.ExecuteAllowedById.Should().Be(admin.Id);
        red.ExecuteAllowedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        // Aplikacija: dugme otključano, bez starog „od …" datuma
        var posle = await KodUslugodavcaAsync(id, provajderId);
        posle.CanExecute.Should().BeTrue();
        posle.ExecuteWaitWaived.Should().BeTrue();
        posle.CanExecuteAt.Should().Be(red.ExecuteAllowedAt);

        // Isti red i na klijentovoj strani
        var kodKlijenta = await WithService<BookingService, BookingDto>(
            async svc => (await svc.GetOutgoingAsync(klijentId)).Single(b => b.Id == id));
        kodKlijenta.CanExecute.Should().BeTrue();
    }

    [Fact]
    public async Task SkinutoCekanje_UslugodavacDobijaObavestenjeJednom()
    {
        // Admin šalje obaveštenje odmah; podsetnik ga posle ne sme ponoviti.

        // Arrange
        var admin = await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);

        // Act
        await SkiniAsync(id, admin.Id);
        var podsetnik = await WithService<PodsetnikZaIzvrsenje, int>(svc => svc.PosaljiAsync());

        // Assert
        podsetnik.Should().Be(0);
        (await Query(db => db.Notifications.CountAsync(n =>
            n.Kind == NotificationKind.BookingExecutable && n.ReferenceId == id && n.UserId == provajderId)))
            .Should().Be(1);
    }

    // ── 2. Samo potvrđen termin koji još čeka ──────────────────────────────

    [Fact]
    public async Task Skidanje_NaOtkazanomTerminu_Odbija()
    {
        var admin = await Data.CreateAdminAsync();
        var (klijentId, _, listingId) = await PripremiAsync();
        var (booking, _) = await WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateBookingDto { ListingId = listingId }));
        await WithService<BookingService, (bool, string?)>(svc => svc.CancelAsync(booking!.Id, klijentId));

        var (ok, greska, nePostoji) = await SkiniAsync(booking!.Id, admin.Id);

        ok.Should().BeFalse();
        nePostoji.Should().BeFalse();
        greska.Should().Contain("potvrđenom");
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == booking.Id)))
            .ExecuteAllowedAt.Should().BeNull();
    }

    [Fact]
    public async Task Skidanje_NaNepotvrdjenomTerminu_Odbija()
    {
        var admin = await Data.CreateAdminAsync();
        var (klijentId, _, listingId) = await PripremiAsync();
        var (booking, _) = await WithService<BookingService, (BookingDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateBookingDto { ListingId = listingId }));

        var (ok, greska, _) = await SkiniAsync(booking!.Id, admin.Id);

        ok.Should().BeFalse();
        greska.Should().Contain("potvrđenom");
    }

    [Fact]
    public async Task Skidanje_NaIzvrsenomTerminu_Odbija()
    {
        var admin = await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);
        await SkiniAsync(id, admin.Id);
        await IzvrsiAsync(id, provajderId);

        var (ok, greska, _) = await SkiniAsync(id, admin.Id);

        ok.Should().BeFalse();
        greska.Should().Contain("potvrđenom");
        // Nagrada se ne isplaćuje dvaput
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(NagradaZaUslugu);
    }

    [Fact]
    public async Task Skidanje_KadJeCekanjeVecIsteklo_Odbija()
    {
        var admin = await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);
        await Query(db => db.BookingRequests.Where(b => b.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.AcceptedAt, DateTime.UtcNow.AddDays(-30))));

        var (ok, greska, _) = await SkiniAsync(id, admin.Id);

        ok.Should().BeFalse();
        greska.Should().Contain("već isteklo");
    }

    [Fact]
    public async Task Skidanje_NepostojecegTermina_NePostoji()
    {
        var admin = await Data.CreateAdminAsync();
        var (ok, _, nePostoji) = await SkiniAsync(999_999, admin.Id);
        ok.Should().BeFalse();
        nePostoji.Should().BeTrue();
    }

    [Fact]
    public async Task VracanjeCekanja_IzvrsenjePonovoZakljucano()
    {
        var admin = await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);
        await SkiniAsync(id, admin.Id);

        var (ok, greska, _) = await WithService<BookingService, (bool, string?, bool)>(
            svc => svc.VratiCekanjeAsync(id, admin.Id));

        ok.Should().BeTrue(greska);
        (await IzvrsiAsync(id, provajderId)).Greska.Should().Contain("tek za");
        (await KodUslugodavcaAsync(id, provajderId)).ExecuteWaitWaived.Should().BeFalse();
    }

    // ── 3. Pouzdani korisnici ──────────────────────────────────────────────

    [Fact]
    public async Task ObaPouzdana_PotvrdaSkidaCekanje()
    {
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        await OznaciPouzdanAsync(klijentId);
        await OznaciPouzdanAsync(provajderId);

        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);

        var red = await Query(db => db.BookingRequests.SingleAsync(b => b.Id == id));
        red.ExecuteAllowedAt.Should().NotBeNull();
        red.ExecuteAllowedById.Should().BeNull("automatski, ne admin");
        (await IzvrsiAsync(id, provajderId)).Booking.Should().NotBeNull();

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(NagradaZaUslugu);
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(NagradaProvajderu);
    }

    [Fact]
    public async Task ObaPouzdana_PodsetnikJavljaUslugodavcu()
    {
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        await OznaciPouzdanAsync(klijentId);
        await OznaciPouzdanAsync(provajderId);
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);

        (await WithService<PodsetnikZaIzvrsenje, int>(svc => svc.PosaljiAsync())).Should().Be(1);

        var obavestenje = await Query(db => db.Notifications.SingleAsync(n =>
            n.Kind == NotificationKind.BookingExecutable && n.ReferenceId == id));
        obavestenje.Body.Should().Contain("odmah");
    }

    [Fact]
    public async Task SamoJedanPouzdan_CekanjeOstaje()
    {
        // Čekanje je brana od para naloga u dogovoru; nepoznata druga strana
        // je upravo taj rizik, pa jedan pouzdan učesnik nije dovoljan.
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        await OznaciPouzdanAsync(provajderId);

        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);

        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == id)))
            .ExecuteAllowedAt.Should().BeNull();
        (await IzvrsiAsync(id, provajderId)).Greska.Should().Contain("tek za");
    }

    // ── 4. HTTP: samo admin ────────────────────────────────────────────────

    [Fact]
    public async Task Api_Neadmin_DobijaZabranu_AdminProlazi()
    {
        await Data.CreateAdminAsync();
        var (klijentId, provajderId, listingId) = await PripremiAsync();
        var id = await PotvrdjenAsync(klijentId, provajderId, listingId);
        var putanja = $"/api/admin/bookings/{id}/allow-execute-now";

        // Ni uslugodavac ni klijent ne mogu sami sebi da skinu čekanje
        var uslugodavac = await PrijavljenAsync("provajder@test.rs");
        (await uslugodavac.PostAsync(putanja, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await uslugodavac.PatchAsync($"/api/admin/users/{provajderId}/trusted?trusted=true", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Factory.CreateClient().PostAsync(putanja, null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Query(db => db.BookingRequests.SingleAsync(b => b.Id == id)))
            .ExecuteAllowedAt.Should().BeNull();

        // Admin: lista na čekanju → skini → nestaje sa liste
        var admin = await PrijavljenAsync("admin.test@usluzionica.rs");
        var lista = await admin.GetStringAsync("/api/admin/bookings/confirmed");
        lista.Should().Contain($"\"id\":{id}");

        (await admin.PostAsync(putanja, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsync(putanja, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetStringAsync("/api/admin/bookings/confirmed")).Should().NotContain($"\"id\":{id}");
        (await admin.GetStringAsync("/api/admin/bookings/confirmed?waiting=false"))
            .Should().Contain("\"executeAllowedByName\":\"Admin Testić\"");
    }
}
