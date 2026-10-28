using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// „Preporučeni oglasi" na dnu detalja — <c>ListingService.GetSimilarAsync</c>
/// i <c>GET /api/listings/{id}/similar</c>.
///
/// Kandidati idu kroz isti osnovni upit kao pretraga, pa ovi testovi
/// proveravaju da filteri pretrage (status, ugašen nalog, blokade) zaista
/// važe i ovde, plus ono što je novo: dopuna iz srodnih kategorija i redosled.
/// </summary>
public class ListingSimilarTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // Podkategorije „Beauty" (Id 1) iz seed podataka.
    private const int FrizerstvoZene    = 14;
    private const int FrizerstvoMuskarci = 15;
    private const int Beauty            = 1;

    // Druga roditeljska kategorija — nikad ne sme da uđe u preporuke za Beauty.
    private const int Majstori          = 4;

    private Task<List<ListingDto>?> Slicni(int id, int take = 8, string? gledalacId = null) =>
        WithService<ListingService, List<ListingDto>?>(svc => svc.GetSimilarAsync(id, take, gledalacId));

    private async Task BoostujAsync(int listingId, string providerId)
    {
        await WithService<BoostService>(async svc =>
        {
            var (ok, err) = await svc.BoostListingAsync(listingId, providerId,
                new UsluzionicaServer.DTOs.Tokens.BoostListingDto { TokensToSpend = 10m, DurationDays = 7 });
            ok.Should().BeTrue(err);
        });
    }

    private Task PostaviOcenuAsync(int providerProfileId, decimal ocena) =>
        Query(db => db.ProviderProfiles.Where(p => p.Id == providerProfileId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AverageRating, ocena)));

    // ── Kategorija ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Slicni_IstaKategorija_BezSamogOglasa()
    {
        var (provider, _) = await Data.CreateProviderAsync("slicni1@test.rs");
        var izvor  = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje",  categoryId: FrizerstvoZene);
        var drugi  = await Data.CreateActiveListingAsync(provider.Id, "Feniranje",       categoryId: FrizerstvoZene);
        var treci  = await Data.CreateActiveListingAsync(provider.Id, "Pramenovi",       categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(provider.Id, "Popravka bojlera", categoryId: Majstori);

        var rezultat = await Slicni(izvor);

        rezultat.Should().NotBeNull();
        rezultat!.Select(l => l.Id).Should().BeEquivalentTo([drugi, treci],
            "isti oglas se ne preporučuje samom sebi, a druga roditeljska kategorija ne ulazi");
    }

    [Fact]
    public async Task Slicni_PremaloUKategoriji_DopunjujeIzSestrinske_IstaIdePrva()
    {
        // Oglas iz srodne kategorije je istaknut, ali i dalje ide POSLE oglasa
        // iz tačno iste kategorije — dopuna je dopuna, ne konkurencija.
        var (provider, _) = await Data.CreateProviderAsync("slicni2@test.rs", tokenBalance: 100m);
        var izvor     = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje", categoryId: FrizerstvoZene);
        var ista      = await Data.CreateActiveListingAsync(provider.Id, "Feniranje",      categoryId: FrizerstvoZene);
        var sestrinska = await Data.CreateActiveListingAsync(provider.Id, "Muško šišanje", categoryId: FrizerstvoMuskarci);
        var roditelj  = await Data.CreateActiveListingAsync(provider.Id, "Beauty paket",   categoryId: Beauty);
        await BoostujAsync(sestrinska, provider.Id);

        var rezultat = await Slicni(izvor);

        rezultat!.Select(l => l.Id).Should().HaveCount(3);
        rezultat[0].Id.Should().Be(ista);
        rezultat.Skip(1).Select(l => l.Id).Should().BeEquivalentTo([sestrinska, roditelj]);
    }

    [Fact]
    public async Task Slicni_DovoljnoUKategoriji_NeDopunjuje()
    {
        var (provider, _) = await Data.CreateProviderAsync("slicni3@test.rs");
        var izvor = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje", categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(provider.Id, "Feniranje",     categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(provider.Id, "Pramenovi",     categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(provider.Id, "Muško šišanje", categoryId: FrizerstvoMuskarci);

        var rezultat = await Slicni(izvor, take: 2);

        rezultat!.Should().HaveCount(2)
            .And.OnlyContain(l => l.CategoryId == FrizerstvoZene);
    }

    [Fact]
    public async Task Slicni_OglasURoditeljskoj_DopunjujeIzPodkategorija()
    {
        var (provider, _) = await Data.CreateProviderAsync("slicni4@test.rs");
        var izvor = await Data.CreateActiveListingAsync(provider.Id, "Beauty paket", categoryId: Beauty);
        var dete  = await Data.CreateActiveListingAsync(provider.Id, "Feniranje",    categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(provider.Id, "Popravka bojlera", categoryId: Majstori);

        var rezultat = await Slicni(izvor);

        rezultat!.Should().ContainSingle().Which.Id.Should().Be(dete);
    }

    // ── Filteri ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Slicni_PauziranIUgasenNalog_NeUlaze()
    {
        var (provider, _) = await Data.CreateProviderAsync("slicni5@test.rs");
        var (ugasen, _)   = await Data.CreateProviderAsync("slicni5b@test.rs");
        var izvor    = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje", categoryId: FrizerstvoZene);
        var aktivan  = await Data.CreateActiveListingAsync(provider.Id, "Feniranje",      categoryId: FrizerstvoZene);
        var pauziran = await Data.CreateActiveListingAsync(provider.Id, "Pramenovi",      categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(ugasen.Id, "Šišanje kod kuće", categoryId: FrizerstvoZene);

        await WithService<ListingService>(async svc =>
        {
            var (ok, err) = await svc.UpdateStatusAsync(pauziran, provider.Id, "Paused");
            ok.Should().BeTrue(err);
        });
        await Query(db => db.Users.Where(u => u.Id == ugasen.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));

        var rezultat = await Slicni(izvor);

        rezultat!.Should().ContainSingle().Which.Id.Should().Be(aktivan);
    }

    [Fact]
    public async Task Slicni_BlokiranUslugodavac_NeUlazi()
    {
        var gledalac = await Data.CreateConfirmedUserAsync("gledalac@test.rs", "Gledalac Testić");
        var (provider, _)  = await Data.CreateProviderAsync("slicni6@test.rs");
        var (blokiran, _)  = await Data.CreateProviderAsync("slicni6b@test.rs");
        var izvor   = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje", categoryId: FrizerstvoZene);
        var vidljiv = await Data.CreateActiveListingAsync(provider.Id, "Feniranje",      categoryId: FrizerstvoZene);
        var skriven = await Data.CreateActiveListingAsync(blokiran.Id, "Pramenovi",      categoryId: FrizerstvoZene);

        // Pozitivna kontrola: anoniman gledalac vidi oba.
        (await Slicni(izvor))!.Select(l => l.Id).Should().Contain(skriven);

        var (uspeh, greska) = await WithService<BlockService, (bool, string?)>(
            svc => svc.BlokirajAsync(gledalac.Id, blokiran.Id));
        uspeh.Should().BeTrue(greska ?? string.Empty);

        var rezultat = await Slicni(izvor, gledalacId: gledalac.Id);

        rezultat!.Should().ContainSingle().Which.Id.Should().Be(vidljiv);
    }

    [Fact]
    public async Task Slicni_BlokiranVlasnikIzvora_VracaNull()
    {
        // Detalj tom gledaocu vraća 404 — preporuke ne smeju da otkriju da
        // oglas postoji.
        var gledalac = await Data.CreateConfirmedUserAsync("gledalac2@test.rs", "Gledalac Testić");
        var (provider, _) = await Data.CreateProviderAsync("slicni7@test.rs");
        var izvor = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje", categoryId: FrizerstvoZene);

        await WithService<BlockService>(async svc =>
        {
            var (ok, err) = await svc.BlokirajAsync(provider.Id, gledalac.Id);
            ok.Should().BeTrue(err);
        });

        (await Slicni(izvor, gledalacId: gledalac.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Slicni_NepostojeciOglas_VracaNull()
    {
        (await Slicni(987654)).Should().BeNull();
    }

    // ── Redosled ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Slicni_Redosled_IstaknutiPaIstiGradPaOcena()
    {
        var (izvorni,  _)      = await Data.CreateProviderAsync("red1@test.rs", tokenBalance: 100m);
        var (dobar,    dobarP) = await Data.CreateProviderAsync("red2@test.rs");
        var (losiji,   losijiP) = await Data.CreateProviderAsync("red3@test.rs");

        var izvor = await Data.CreateActiveListingAsync(
            izvorni.Id, "Žensko šišanje", location: "Novi Sad", categoryId: FrizerstvoZene);

        // Drugi grad, ali najbolja ocena.
        var drugiGrad = await Data.CreateActiveListingAsync(
            dobar.Id, "Feniranje", location: "Subotica", categoryId: FrizerstvoZene);
        // Isti grad, slabija ocena — ide ispred drugog grada.
        var istiGrad = await Data.CreateActiveListingAsync(
            losiji.Id, "Pramenovi", location: "Novi Sad", categoryId: FrizerstvoZene);
        // Istaknut, drugi grad — ide ispred svih.
        var istaknut = await Data.CreateActiveListingAsync(
            izvorni.Id, "Šiške", location: "Subotica", categoryId: FrizerstvoZene);
        await BoostujAsync(istaknut, izvorni.Id);

        await PostaviOcenuAsync(dobarP, 4.9m);
        await PostaviOcenuAsync(losijiP, 3.1m);

        var rezultat = await Slicni(izvor);

        rezultat!.Select(l => l.Id).Should().Equal(istaknut, istiGrad, drugiGrad);
    }

    [Fact]
    public async Task Slicni_IstiGradObuhvataOpstinu()
    {
        // „Beograd — Vračar" i „Beograd — Zemun" su isti grad; „Novi Sad" nije.
        var (provider, _) = await Data.CreateProviderAsync("opstina@test.rs");
        var izvor = await Data.CreateActiveListingAsync(
            provider.Id, "Žensko šišanje", location: "Beograd — Vračar", categoryId: FrizerstvoZene);
        await Data.CreateActiveListingAsync(
            provider.Id, "Feniranje", location: "Novi Sad", categoryId: FrizerstvoZene);
        await Task.Delay(20);
        var zemun = await Data.CreateActiveListingAsync(
            provider.Id, "Pramenovi", location: "Beograd — Zemun", categoryId: FrizerstvoZene);
        await Task.Delay(20);
        // Najnoviji, ali drugi grad — bez prednosti grada bi išao prvi.
        await Data.CreateActiveListingAsync(
            provider.Id, "Šiške", location: "Novi Sad", categoryId: FrizerstvoZene);

        var rezultat = await Slicni(izvor);

        rezultat![0].Id.Should().Be(zemun);
    }

    // ── Endpoint ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Endpoint_VracaNizKartica_I404ZaNepostojeci()
    {
        var (provider, _) = await Data.CreateProviderAsync("endpoint@test.rs");
        var izvor = await Data.CreateActiveListingAsync(provider.Id, "Žensko šišanje", categoryId: FrizerstvoZene);
        var drugi = await Data.CreateActiveListingAsync(provider.Id, "Feniranje",      categoryId: FrizerstvoZene);

        var c = Factory.CreateClient();

        var ok = await c.GetAsync($"/api/listings/{izvor}/similar?take=8");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.ValueKind.Should().Be(JsonValueKind.Array);
        data.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).Should().Equal(drugi);

        (await c.GetAsync("/api/listings/987654/similar")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
