using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.Infrastructure.Search;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo: denormalizovane Search* kolone se održavaju SAME OD SEBE,
/// kroz AppDbContext.SaveChanges.
///
/// Zašto je to vredno testa: da se indeksiranje radi u servisima, svaki budući
/// put pisanja bio bi jedna zaboravljena linija od tihog kvara — oglas bi
/// postojao u bazi, izgledao ispravno na ekranu, a pretraga ga ne bi nalazila.
/// Takav kvar se ne vidi ni u jednom logu.
/// </summary>
public class SearchIndexMaintenanceTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task KreiranjeOglasa_AutomatskiPopunjavaSearchKolone()
    {
        // Arrange
        var (provider, _) = await Data.CreateProviderAsync("indeks@test.rs");

        // Act — običan put kreiranja oglasa. NIJEDNA linija ne pominje Search*.
        var listingId = await Data.CreateActiveListingAsync(
            provider.Id,
            title:    "Šišanje i feniranje",
            location: TestData.ValidCity);

        // Assert — čitamo kroz nov DbContext (vidi Query u baznoj klasi)
        var listing = await Query(db => db.Listings.SingleAsync(l => l.Id == listingId));

        listing.SearchTitle.Should().Be("sisanje i feniranje");
        listing.SearchLocation.Should().Be("subotica");
        listing.SearchVersion.Should().Be(SearchNormalizer.Version);
    }

    [Fact]
    public async Task IzmenaOglasa_OsvezavaSearchKolone()
    {
        // Ovo je važniji slučaj od kreiranja: lako je setiti se indeksa pri
        // upisu, a zaboraviti ga pri izmeni. Tada oglas ostaje pretraživ po
        // STAROM naslovu, a po novom ne — najgora vrsta kvara jer izgleda
        // kao da pretraga „ponekad ne radi".

        // Arrange
        var (provider, _) = await Data.CreateProviderAsync("indeks2@test.rs");
        var listingId = await Data.CreateActiveListingAsync(provider.Id, title: "Stari naslov");

        // Act
        await WithService<ListingService>(async svc =>
        {
            await svc.UpdateAsync(listingId, provider.Id, new UpdateListingDto
            {
                Title       = "Novi naslov sa Đorđem",
                Description = "Opis",
                Location    = TestData.ValidCity,
                CategoryId  = TestData.SeededCategoryId,
                PriceMode   = UsluzionicaServer.Domain.Enums.PriceMode.Fixed,
                FixedPrice  = 1500m
            });
        });

        // Assert
        var listing = await Query(db => db.Listings.SingleAsync(l => l.Id == listingId));

        listing.SearchTitle.Should().Be("novi naslov sa djordjem");
        listing.SearchTitle.Should().NotContain("stari");
    }

    [Fact]
    public async Task RegistracijaKorisnika_AutomatskiPopunjavaSearchName()
    {
        // Act
        var user = await Data.CreateConfirmedUserAsync(
            "korisnik.dijakritika@test.rs", fullName: "Miloš Đurđević");

        // Assert
        var stored = await Query(db => db.Users.SingleAsync(u => u.Id == user.Id));

        // Oba „đ" postaju „dj" — dokaz da je original sačuvan sa dijakritikom.
        // Da je ime negde usput osiromašeno na ASCII, fold bi dao „durdevic".
        stored.SearchName.Should().Be("milos djurdjevic");
        stored.SearchVersion.Should().Be(SearchNormalizer.Version);
    }

    [Fact]
    public async Task CirilicniNaslov_SeIndeksiraKaoLatinica()
    {
        // Arrange
        var (provider, _) = await Data.CreateProviderAsync("cirilica@test.rs");

        // Act
        var listingId = await Data.CreateActiveListingAsync(
            provider.Id, title: "Фризерски салон");

        // Assert
        var listing = await Query(db => db.Listings.SingleAsync(l => l.Id == listingId));

        listing.SearchTitle.Should().Be("frizerski salon",
            "ćirilica i latinica moraju završiti u istom obliku da bi se mogle unakrsno pretraživati");
    }
    [Fact]
    public async Task IzmenaGradova_OsvezavaSearchLocationIGradove()
    {
        var (provider, _) = await Data.CreateProviderAsync("indeks.gradovi@test.rs");
        var listingId = await Data.CreateActiveListingAsync(provider.Id, title: "Moler");

        await WithService<ListingService>(async svc =>
        {
            var (ok, err) = await svc.UpdateAsync(listingId, provider.Id, new UpdateListingDto
            {
                Title       = "Moler",
                Description = "Opis",
                Cities      = ["Čačak", "Novi Sad"],
                IsOnline    = true,
                CategoryId  = TestData.SeededCategoryId,
                PriceMode   = UsluzionicaServer.Domain.Enums.PriceMode.Fixed,
                FixedPrice  = 1500m
            });
            ok.Should().BeTrue(err);
        });

        var listing = await Query(db => db.Listings.Include(l => l.Cities).SingleAsync(l => l.Id == listingId));

        listing.SearchLocation.Should().Be("cacak novi sad online onlajn");
        listing.Location.Should().Be("Čačak");
        listing.Cities.OrderBy(c => c.SortOrder).Select(c => c.SearchCity)
               .Should().Equal("cacak", "novi sad");
    }

    [Fact]
    public async Task PromenaStatusa_BezUcitanihGradova_NeBriseGradoveIzIndeksa()
    {
        // UpdateStatusAsync učitava oglas BEZ gradova. Da RefreshSearchIndex
        // ne dovuče gradove, SearchLocation posle pauze ne bi imao gradove i
        // oglas se više ne bi nalazio po gradu u tekstu.
        var (provider, _) = await Data.CreateProviderAsync("indeks.status@test.rs");
        var listingId = await Data.CreateActiveListingAsync(provider.Id, title: "Moler");

        await WithService<ListingService>(async svc =>
        {
            (await svc.UpdateAsync(listingId, provider.Id, new UpdateListingDto
            {
                Title       = "Moler",
                Description = "Opis",
                Cities      = ["Čačak", "Novi Sad"],
                CategoryId  = TestData.SeededCategoryId,
                PriceMode   = UsluzionicaServer.Domain.Enums.PriceMode.Fixed,
                FixedPrice  = 1500m
            })).Success.Should().BeTrue();
        });

        await WithService<ListingService>(async svc =>
        {
            (await svc.UpdateStatusAsync(listingId, provider.Id, "Paused")).Success.Should().BeTrue();
            (await svc.UpdateStatusAsync(listingId, provider.Id, "Active")).Success.Should().BeTrue();
        });

        var listing = await Query(db => db.Listings.SingleAsync(l => l.Id == listingId));
        listing.SearchLocation.Should().Be("cacak novi sad");
    }
}
