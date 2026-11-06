using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti ponašanje pretrage protiv PRAVE SQL Server baze.
///
/// Zašto ne unit testovi: pretraga se oslanja na `LIKE` semantiku, na to da
/// EF prevede predikate u SQL, i na indeksirane `varchar` kolone. EF InMemory
/// bi ovde davao lažno zeleno — nijedna od tih stvari tamo ne postoji.
///
/// Svaki test odgovara jednom slučaju koji je RANIJE PADAO. Stara pretraga je
/// radila `LIKE '%ceo upit%'` nad Title i Description.
/// </summary>
public class ListingSearchTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private Task<PagedResult<ListingDto>> Search(string? q, string? city = null) =>
        WithService<ListingService, PagedResult<ListingDto>>(svc =>
            svc.SearchAsync(new ListingQueryParams { Q = q, City = city, Page = 1, PageSize = 50 }));

    // ── Dijakritika ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_BezDijakritike_NalaziOglasSaDijakritikom()
    {
        // RANIJE PADALO: "sisanje" nije nalazilo "Šišanje" jer je LIKE zavisio
        // od collation-a baze, koji je akcenat-osetljiv.
        var (provider, _) = await Data.CreateProviderAsync("frizer@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Šišanje i feniranje");

        var result = await Search("sisanje");

        result.Items.Should().ContainSingle()
              .Which.Title.Should().Be("Šišanje i feniranje");
    }

    [Fact]
    public async Task Pretraga_SaDijakritikom_NalaziOglasBezNje()
    {
        // Obrnut smer — oglas napisan bez dijakritike, upit sa njom.
        var (provider, _) = await Data.CreateProviderAsync("frizer2@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Sisanje na brzinu");

        var result = await Search("šišanje");

        result.Items.Should().ContainSingle();
    }

    // ── Ćirilica ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_Cirilicom_NalaziLatinicniOglas()
    {
        // RANIJE PADALO: ćirilični upit nije nalazio ništa, nikad.
        var (provider, _) = await Data.CreateProviderAsync("cir@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Frizerski salon Luna");

        var result = await Search("фризерски");

        result.Items.Should().ContainSingle()
              .Which.Title.Should().Be("Frizerski salon Luna");
    }

    [Fact]
    public async Task Pretraga_Latinicom_NalaziCirilicniOglas()
    {
        var (provider, _) = await Data.CreateProviderAsync("cir2@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Фризерски салон");

        var result = await Search("frizerski");

        result.Items.Should().ContainSingle();
    }

    // ── Više tokena ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_SaDvaTokena_TraziOBA()
    {
        // RANIJE PADALO NAJTEŽE: ceo upit je išao kao jedan LIKE, pa
        // "frizer novi" nije nalazilo "Frizerski salon" u "Novi Sad" —
        // taj tačan niz znakova ne postoji ni u naslovu ni u opisu.
        var (provider, _) = await Data.CreateProviderAsync("dva@test.rs");
        await Data.CreateActiveListingAsync(
            provider.Id, title: "Frizerski salon", location: "Novi Sad");

        var result = await Search("frizerski novi");

        result.Items.Should().ContainSingle(
            "oba tokena se poklapaju — jedan u naslovu, drugi u lokaciji");
    }

    [Fact]
    public async Task Pretraga_SaDvaTokena_NeVracaOglasSaSamoJednim()
    {
        // Negativan par prethodnog: AND semantika mora zaista biti AND na
        // najužem sloju. Bez ovoga bi "frizerski novi" vraćalo i frizere iz
        // drugih gradova, pa filter ne bi značio ništa.
        var (provider, _) = await Data.CreateProviderAsync("dva2@test.rs");
        await Data.CreateActiveListingAsync(
            provider.Id, title: "Frizerski salon", location: "Subotica");
        // Još dva oglasa da sloj 1a ima dovoljno rezultata i ne padne u fuzzy.
        await Data.CreateActiveListingAsync(provider.Id, title: "Frizerski studio", location: "Subotica");
        await Data.CreateActiveListingAsync(provider.Id, title: "Frizerski kutak", location: "Subotica");

        var result = await Search("frizerski novi");

        result.Items.Should().BeEmpty("nijedan oglas nije u gradu koji počinje sa 'novi'");
    }

    // ── Tipfeleri ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_SaTipfelerom_NalaziOglas()
    {
        // RANIJE PADALO: "vodoinstalter" (nedostaje 'a') nije nalazilo ništa.
        var (provider, _) = await Data.CreateProviderAsync("vodo@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Vodoinstalater Marko");

        var result = await Search("vodoinstalter");

        result.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Pretraga_SaZamenjenimSlovima_NalaziOglas()
    {
        // Transpozicija — najčešća greška pri kucanju. Zbog OSA rastojanja
        // se naplaćuje kao JEDNA izmena; čist Levenshtein bi je naplatio kao
        // dve i izbacio iz praga za dužinu 6.
        var (provider, _) = await Data.CreateProviderAsync("transp@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Frizer Milan");

        var result = await Search("frizre");

        result.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Pretraga_BesmislenogUpita_VracaNula()
    {
        // NAJVAŽNIJI TEST U FAJLU.
        //
        // Fuzzy sloj koji vraća smeće gori je od nikakvog — korisnik izgubi
        // poverenje u pretragu i prestane da je koristi. Prag skora (0.45)
        // postoji baš zbog ovoga.
        var (provider, _) = await Data.CreateProviderAsync("nula@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Frizerski salon");
        await Data.CreateActiveListingAsync(provider.Id, title: "Vodoinstalater");

        var result = await Search("qwertyasdf");

        result.Items.Should().BeEmpty();
        result.Total.Should().Be(0);
    }

    // ── Filter grada ───────────────────────────────────────────────────────

    [Fact]
    public async Task FilterGrada_BezDijakritike_NalaziGradSaNjom()
    {
        // RANIJE PADALO: filter je bio `l.Location == city`, tačna jednakost.
        // "cacak" nije nalazilo "Čačak".
        var (provider, _) = await Data.CreateProviderAsync("grad@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Moler", location: "Čačak");
        await Data.CreateActiveListingAsync(provider.Id, title: "Moler 2", location: "Subotica");

        var result = await Search(q: null, city: "cacak");

        result.Items.Should().ContainSingle()
              .Which.Location.Should().Be("Čačak");
    }

    // ── Bezbednost upita ───────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_SaDzokerZnakomUPojmu_NeSiriRezultat()
    {
        // Bez zaštite bi "%" bio LIKE džoker i "prvi%" bi vratio SVE oglase.
        //
        // Zaštita je dvostruka: Fold() izbacuje sve što nije alfanumerik (pa %
        // postaje razmak i nestaje), a EscapeLike dodatno neutrališe % _ [ \
        // ako bi se pravila preklapanja ikad promenila. Praktično se ovde
        // testira prvi sloj — do drugog upit ne stigne.
        var (provider, _) = await Data.CreateProviderAsync("escape@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Prvi oglas");
        await Data.CreateActiveListingAsync(provider.Id, title: "Drugi oglas");
        await Data.CreateActiveListingAsync(provider.Id, title: "Treci oglas");

        var result = await Search("prvi%");

        result.Items.Should().ContainSingle()
              .Which.Title.Should().Be("Prvi oglas");
    }

    [Fact]
    public async Task Pretraga_SamoSpecijalnihZnakova_SeTretiraKaoPrazanUpit()
    {
        // "%" ili "!!!" nemaju nijedan pretraživ znak — posle preklapanja
        // ostaje prazan string. Tada je to isto kao da korisnik nije ništa
        // ukucao, pa se vraćaju svi oglasi. Dokumentovano ponašanje, ne propust.
        var (provider, _) = await Data.CreateProviderAsync("spec@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Prvi oglas");
        await Data.CreateActiveListingAsync(provider.Id, title: "Drugi oglas");

        var result = await Search("%");

        result.Total.Should().Be(2);
    }

    // ── Prazan upit ────────────────────────────────────────────────────────

    [Fact]
    public async Task PrazanUpit_VracaSveAktivneOglase()
    {
        var (provider, _) = await Data.CreateProviderAsync("prazan@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Prvi");
        await Data.CreateActiveListingAsync(provider.Id, title: "Drugi");

        var result = await Search(q: null);

        result.Total.Should().Be(2);
    }

    // ── Redosled reči u upitu ──────────────────────────────────────────────

    [Theory]
    [InlineData("razvoj web aplikacija")]
    [InlineData("web aplikacija razvoj")]
    [InlineData("aplikacija razvoj web")]
    [InlineData("web razvoj")]
    public async Task Pretraga_RedosledReciNijeBitan(string upit)
    {
        // Upit se razlaže na tokene i svaki se traži zasebno, pa je redosled
        // nebitan po konstrukciji. Stara pretraga je tražila ceo upit kao
        // jedan niz znakova, gde je redosled bio presudan.
        var (provider, _) = await Data.CreateProviderAsync("redosled@test.rs");
        await Data.CreateActiveListingAsync(provider.Id, title: "Web razvoj aplikacija");

        var result = await Search(upit);

        result.Items.Should().ContainSingle();
    }

    // ── Pretraga po imenu uslugodavca ──────────────────────────────────────

    [Fact]
    public async Task Pretraga_PoImenuUslugodavca_NalaziNjegoveOglase()
    {
        // Korisnik često pamti majstora po imenu, ne po naslovu oglasa.
        var (milan, _) = await Data.CreateProviderAsync(
            "milan@test.rs", fullName: "Milan Petrović");
        var (jovan, _) = await Data.CreateProviderAsync(
            "jovan@test.rs", fullName: "Jovan Nikolić");

        await Data.CreateActiveListingAsync(milan.Id, title: "Zamena kvačila");
        await Data.CreateActiveListingAsync(jovan.Id, title: "Zamena guma");

        var result = await Search("milan");

        result.Items.Should().ContainSingle()
              .Which.Title.Should().Be("Zamena kvačila");
    }

    [Fact]
    public async Task Pretraga_ImeUslugodavcaPlusPojam_SuzavaNaOba()
    {
        // Traženi scenario: „automehanika milan" → samo Milanovi oglasi iz
        // automehanike, ne svi Milanovi i ne sva automehanika.
        var (milan, _) = await Data.CreateProviderAsync(
            "milan2@test.rs", fullName: "Milan Petrović");
        var (marko, _) = await Data.CreateProviderAsync(
            "marko2@test.rs", fullName: "Marko Marković");

        var trazeni = await Data.CreateActiveListingAsync(milan.Id, title: "Automehanika i dijagnostika");
        await Data.CreateActiveListingAsync(milan.Id, title: "Prevoz robe");            // Milan, ali ne automehanika
        await Data.CreateActiveListingAsync(marko.Id, title: "Automehanika Marko");      // automehanika, ali ne Milan

        var result = await Search("automehanika milan");

        result.Items.Should().ContainSingle()
              .Which.Id.Should().Be(trazeni);
    }

    [Fact]
    public async Task Pretraga_PoPrezimenuUslugodavca_TakodjeRadi()
    {
        var (provider, _) = await Data.CreateProviderAsync(
            "prezime@test.rs", fullName: "Milan Petrović");
        await Data.CreateActiveListingAsync(provider.Id, title: "Zamena kvačila");

        // I bez dijakritike — „petrovic" mora naći „Petrović".
        var result = await Search("petrovic");

        result.Items.Should().ContainSingle();
    }

    // ── Redosled ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_BoostovanOglasIdePrviPriIstomPoklapanju()
    {
        // Boost je plaćena usluga — mora se videti u redosledu, ali tek kad je
        // relevantnost jednaka. Ne sme nadjačati bolje poklapanje.
        var (provider, _) = await Data.CreateProviderAsync("boost@test.rs", tokenBalance: 100m);

        var obican    = await Data.CreateActiveListingAsync(provider.Id, title: "Moler Petar");
        var boostovan = await Data.CreateActiveListingAsync(provider.Id, title: "Moler Marko");
        await Data.CreateActiveListingAsync(provider.Id, title: "Moler Nikola");

        await WithService<BoostService>(async svc =>
        {
            var (ok, err) = await svc.BoostListingAsync(boostovan, provider.Id,
                new UsluzionicaServer.DTOs.Tokens.BoostListingDto
                {
                    TokensToSpend = 10m, DurationDays = 7
                });
            ok.Should().BeTrue(err);
        });

        var result = await Search("moler");

        result.Items.Should().HaveCount(3);
        result.Items[0].Id.Should().Be(boostovan);
        result.Items.Select(i => i.Id).Should().Contain(obican);
    }

    // ── Početna: „Istaknuto" i „Novo" (A3) ─────────────────────────────────

    private async Task<int> BoostujAsync(int listingId, string providerId)
    {
        await WithService<BoostService>(async svc =>
        {
            var (ok, err) = await svc.BoostListingAsync(listingId, providerId,
                new UsluzionicaServer.DTOs.Tokens.BoostListingDto { TokensToSpend = 10m, DurationDays = 7 });
            ok.Should().BeTrue(err);
        });
        return listingId;
    }

    private Task<PagedResult<ListingDto>> Pocetna(bool? isBoosted = null, string? sort = null) =>
        WithService<ListingService, PagedResult<ListingDto>>(svc =>
            svc.SearchAsync(new ListingQueryParams { IsBoosted = isBoosted, Sort = sort, Page = 1, PageSize = 50 }));

    [Fact]
    public async Task Istaknuto_VracaSamoBoostovaneOglase()
    {
        // RANIJE PADALO: `?isBoosted=true` je tiho ignorisan, pa je „Istaknuto"
        // dobijalo SVE oglase — isto što i „Novo".
        var (provider, _) = await Data.CreateProviderAsync("istaknuto@test.rs", tokenBalance: 100m);
        var boostovan = await BoostujAsync(
            await Data.CreateActiveListingAsync(provider.Id, title: "Istaknut oglas"), provider.Id);
        await Data.CreateActiveListingAsync(provider.Id, title: "Običan oglas");

        var result = await Pocetna(isBoosted: true);

        result.Items.Should().ContainSingle().Which.Id.Should().Be(boostovan);
    }

    [Fact]
    public async Task Istaknuto_IstekaoBoost_NeUlazi()
    {
        // Zastavicu IsBoosted gasi pozadinski servis jednom na sat — do tada
        // istekao boost ne sme da stoji u „Istaknuto".
        var (provider, _) = await Data.CreateProviderAsync("istekao@test.rs", tokenBalance: 100m);
        var id = await BoostujAsync(
            await Data.CreateActiveListingAsync(provider.Id, title: "Boost istekao"), provider.Id);

        await Query(db => db.Listings.Where(l => l.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.BoostExpiresAt, DateTime.UtcNow.AddMinutes(-5))));

        (await Pocetna(isBoosted: true)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Novo_NajnovijiPrvi_BezObziraNaBoost()
    {
        // RANIJE PADALO: „Novo" je dobijalo istaknute na vrhu, početna ih je
        // izbacivala kao već prikazane — i sekcija je ostajala prazna.
        var (provider, _) = await Data.CreateProviderAsync("novo@test.rs", tokenBalance: 100m);
        await BoostujAsync(await Data.CreateActiveListingAsync(provider.Id, title: "Stari istaknut"), provider.Id);
        await Task.Delay(20);
        var najnoviji = await Data.CreateActiveListingAsync(provider.Id, title: "Najnoviji oglas");

        var result = await Pocetna(sort: "newest");

        result.Items.First().Id.Should().Be(najnoviji);
    }
    // ── Online oglas i više gradova ────────────────────────────────────────

    private Task<(ListingDto? Result, string? Error)> Objavi(
        string providerId, string title, List<string>? cities, bool? online = null) =>
        WithService<ListingService, (ListingDto?, string?)>(svc =>
            svc.CreateAsync(providerId, new CreateListingDto
            {
                Title       = title,
                Description = $"Opis za: {title}",
                Cities      = cities,
                IsOnline    = online,
                CategoryId  = TestData.SeededCategoryId,
                PriceMode   = UsluzionicaServer.Domain.Enums.PriceMode.Fixed,
                FixedPrice  = 2000m
            }));

    private Task<PagedResult<ListingDto>> SearchOnline(string? q, string? city, bool? online) =>
        WithService<ListingService, PagedResult<ListingDto>>(svc =>
            svc.SearchAsync(new ListingQueryParams { Q = q, City = city, Online = online, Page = 1, PageSize = 50 }));

    [Fact]
    public async Task Objava_OnlineBezGrada_Prolazi_ObicanBezGrada_Ne()
    {
        var (provider, _) = await Data.CreateProviderAsync("online1@test.rs");

        var (online, greska) = await Objavi(provider.Id, "Časovi engleskog", cities: [], online: true);
        greska.Should().BeNull();
        online!.IsOnline.Should().BeTrue();
        online.Cities.Should().BeEmpty();
        // Stari klijenti čitaju samo Location — ne sme biti prazno mesto.
        online.Location.Should().Be("Online");

        var (_, greska2) = await Objavi(provider.Id, "Moler", cities: []);
        greska2.Should().NotBeNull();
    }

    [Fact]
    public async Task Objava_Gradovi_SaListe_BezDuplikata_UPisanjuListe()
    {
        var (provider, _) = await Data.CreateProviderAsync("gradovi1@test.rs");

        var (oglas, greska) = await Objavi(provider.Id, "Električar",
            cities: ["novi sad", "Čačak", "Novi Sad", "CACAK"]);

        greska.Should().BeNull();
        oglas!.Cities.Should().Equal("Novi Sad", "Čačak");
        oglas.Location.Should().Be("Novi Sad");
    }

    [Fact]
    public async Task Objava_GradVanListe_IliPrevise_Odbija()
    {
        var (provider, _) = await Data.CreateProviderAsync("gradovi2@test.rs");

        var (_, vanListe) = await Objavi(provider.Id, "Moler", cities: ["Gotham"]);
        vanListe.Should().Contain("Gotham");

        var previse = UsluzionicaServer.Infrastructure.SerbianMunicipalities.All
            .Take(ListingService.MaxGradova + 1).ToList();
        var (_, greska) = await Objavi(provider.Id, "Moler", cities: previse);
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task FilterGrada_NalaziOglasPoBiloKomGradu()
    {
        // Prefiks nad spojenim gradovima bi video samo prvi — zato filter ide
        // nad tabelom gradova.
        var (provider, _) = await Data.CreateProviderAsync("gradovi3@test.rs");
        await Objavi(provider.Id, "Keramičar", cities: ["Subotica", "Čačak"]);

        (await Search(q: null, city: "cacak")).Items.Should().ContainSingle()
            .Which.Cities.Should().Equal("Subotica", "Čačak");
        (await Search(q: null, city: "Novi Sad")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task TekstualniUpit_NalaziOglasPoDrugomGradu()
    {
        var (provider, _) = await Data.CreateProviderAsync("gradovi4@test.rs");
        await Objavi(provider.Id, "Keramičar", cities: ["Subotica", "Čačak"]);

        (await Search("keramicar cacak")).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlineOglas_SeNalaziZaSvakiGrad_SaOznakom()
    {
        var (provider, _) = await Data.CreateProviderAsync("online2@test.rs");
        await Objavi(provider.Id, "Prevodi sa nemačkog", cities: [], online: true);
        await Objavi(provider.Id, "Moler", cities: ["Subotica"]);

        var result = await Search(q: null, city: "Čačak");

        result.Items.Should().ContainSingle()
              .Which.IsOnline.Should().BeTrue();
    }

    [Fact]
    public async Task SamoOnline_VracaSamoOnlineOglase()
    {
        var (provider, _) = await Data.CreateProviderAsync("online3@test.rs");
        var online = (await Objavi(provider.Id, "Časovi gitare", cities: ["Novi Sad"], online: true)).Result!;
        await Objavi(provider.Id, "Časovi klavira", cities: ["Novi Sad"]);

        (await SearchOnline(q: null, city: null, online: true)).Items
            .Should().ContainSingle().Which.Id.Should().Be(online.Id);

        // online=false NIJE „samo uživo" — stari linkovi ne smeju da sakriju deo rezultata.
        (await SearchOnline(q: null, city: null, online: false)).Items.Should().HaveCount(2);

        // Online oglas sa gradom ostaje i u običnoj pretrazi tog grada.
        (await SearchOnline(q: "casovi", city: "Novi Sad", online: null)).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task TekstualniUpit_Online_NalaziOnlineOglas_LatinicomICirilicom()
    {
        var (provider, _) = await Data.CreateProviderAsync("online4@test.rs");
        await Objavi(provider.Id, "Časovi matematike", cities: [], online: true);
        await Objavi(provider.Id, "Časovi fizike", cities: ["Subotica"]);

        (await Search("casovi online")).Items.Should().ContainSingle()
            .Which.Title.Should().Be("Časovi matematike");
        (await Search("часови онлајн")).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task IzmenaIzStareAplikacije_BezGradova_NeBriseOstaleGradove()
    {
        // Stara aplikacija šalje samo Location (prvi grad) i ne zna za IsOnline.
        var (provider, _) = await Data.CreateProviderAsync("stari@test.rs");
        var oglas = (await Objavi(provider.Id, "Vodoinstalater",
            cities: ["Subotica", "Čačak"], online: true)).Result!;

        await WithService<ListingService>(async svc =>
        {
            var (ok, err) = await svc.UpdateAsync(oglas.Id, provider.Id, new UpdateListingDto
            {
                Title       = "Vodoinstalater 0-24",
                Description = "Opis",
                Location    = "Subotica",
                CategoryId  = TestData.SeededCategoryId,
                PriceMode   = UsluzionicaServer.Domain.Enums.PriceMode.Fixed,
                FixedPrice  = 1500m
            });
            ok.Should().BeTrue(err);
        });

        var posle = (await Search(q: null, city: "cacak")).Items.Should().ContainSingle().Subject;
        posle.Cities.Should().Equal("Subotica", "Čačak");
        posle.IsOnline.Should().BeTrue();
    }
}
