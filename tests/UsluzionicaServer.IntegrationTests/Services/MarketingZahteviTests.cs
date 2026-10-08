using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.DTOs.Referrals;
using UsluzionicaServer.Infrastructure.Demo;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Zahtevi marketinga prema aplikaciji (Marketing 14, P0): link pozivnice,
/// izvor korisnika i demo okruženje.
/// </summary>
public class MarketingZahteviTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // ── Razgovor → „Pošalji zahtev" ────────────────────────────────────────

    [Fact]
    public async Task ListaRazgovora_NosiProfilUslugodavca_SamoZaUslugodavca()
    {
        // Ekran razgovora po ovom polju povlači oglase sagovornika za dugme
        // „Pošalji zahtev". Klijent sa druge strane ga NE sme dobiti — on nema
        // oglase, i dugme bi vodilo na prazan spisak.
        var (provajder, profilId) = await Data.CreateProviderAsync("cta-provajder@test.rs");
        var klijent               = await Data.CreateConfirmedUserAsync("cta-klijent@test.rs");

        await WithService<ConversationService, (DTOs.Conversations.ConversationDto?, string?)>(
            svc => svc.GetOrCreateAsync(klijent.Id, provajder.Id));

        var kodKlijenta = await WithService<ConversationService, List<DTOs.Conversations.ConversationDto>>(
            svc => svc.GetConversationsAsync(klijent.Id));
        var kodProvajdera = await WithService<ConversationService, List<DTOs.Conversations.ConversationDto>>(
            svc => svc.GetConversationsAsync(provajder.Id));

        kodKlijenta.Should().ContainSingle().Which.OtherProviderProfileId.Should().Be(profilId);
        kodProvajdera.Should().ContainSingle().Which.OtherProviderProfileId.Should().BeNull();
    }

    // ── P0-1 · Link pozivnice ──────────────────────────────────────────────

    [Fact]
    public async Task LinkPozivnice_VodiNaSajt_ANeNaApi()
    {
        // Ranije: {App:BaseUrl}/register?ref=… — u produkciji api.usluzionica.rs,
        // gde ta stranica ne postoji. Svaka pozivnica je završavala na 404.
        var user = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");

        var kod = await WithService<TokenWalletService, MyReferralCodeDto?>(
            svc => svc.GetMyCodeAsync(user.Id));

        kod!.ShareableLink.Should().Be($"https://usluzionica.rs/pozivnica/{kod.ReferralCode}");
    }

    // ── P0-3 · Kako si čuo za nas ──────────────────────────────────────────

    [Theory]
    [InlineData("tiktok",           "tiktok")]
    [InlineData(" Uslugodavac ",    "uslugodavac")]
    [InlineData("nesto-izmisljeno", null)]           // slobodan tekst bi pokvario brojanje
    [InlineData(null,               null)]           // pitanje je opciono
    public async Task Izvor_SeCuvaKaoKod(string? poslato, string? sacuvano)
    {
        var (ok, greske) = await WithService<AuthService, (bool, string[])>(svc => svc.RegisterAsync(new RegisterRequest
        {
            FullName          = "Nova Korisnica",
            Email             = "nova@test.rs",
            Password          = TestData.DefaultPassword,
            AcceptedPolicy    = true,
            AcquisitionSource = poslato
        }));

        ok.Should().BeTrue(string.Join(", ", greske));
        (await Query(db => db.Users.Where(u => u.Email == "nova@test.rs")
            .Select(u => u.AcquisitionSource).SingleAsync())).Should().Be(sacuvano);
    }

    // ── P0-5 · Demo okruženje ──────────────────────────────────────────────

    private static IConfiguration DemoKonfiguracija() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoSeed:Enabled"]  = "true",
            ["DemoSeed:Password"] = TestData.DefaultPassword
        }).Build();

    private Task PokreniDemoAsync() =>
        WithService<IServiceProvider>(sp => DemoSeed.RunAsync(sp, DemoKonfiguracija(), NullLogger.Instance));

    [Fact]
    public async Task DemoSeed_PraviPotpuneOglaseSaPotvrdjenimOcenama()
    {
        // Prolazi kroz PRAVE servise (aktivacija, pravljenje oglasa), pa ovaj test
        // hvata i to da li demo sadržaj i dalje prolazi validaciju aplikacije.
        await PokreniDemoAsync();

        var oglasi = await Query(db => db.Listings
            .Where(l => l.ProviderProfile.User.Email!.EndsWith("@" + DemoSeed.Domen))
            .Select(l => new { l.Description, l.Status })
            .ToListAsync());

        oglasi.Should().HaveCount(10, "8 uslugodavaca + 2 oglasa demo klijentkinje");
        oglasi.Should().OnlyContain(o => o.Description.Length >= 200,
            "potpun oglas ima opis od bar 200 znakova (Marketing 03 §9)");

        (await Query(db => db.Reviews.AnyAsync(r => r.BookingRequestId == null)))
            .Should().BeFalse("demo ocene moraju proći isto pravilo kao prave");

        var proseci = await Query(db => db.ProviderProfiles
            .Where(p => p.User.Email!.EndsWith("@" + DemoSeed.Domen))
            .Select(p => p.AverageRating).ToListAsync());

        proseci.Should().OnlyContain(p => p >= 4.6m && p <= 5m);
        proseci.Should().Contain(p => p < 5m, "ne svuda 5,0 — to izgleda lažno (06 §3)");
    }

    [Fact]
    public async Task DemoSeed_DrugiStart_NePraviDuplikate()
    {
        await PokreniDemoAsync();
        var posle1 = await Query(db => db.Users.CountAsync());

        await PokreniDemoAsync();

        (await Query(db => db.Users.CountAsync())).Should().Be(posle1);
    }

    [Fact]
    public async Task DemoSeed_RazgovoriSuCitljivi()
    {
        // Poruke se čuvaju šifrovane; demo mora da ide kroz isto šifrovanje,
        // inače bi aplikacija na snimku prikazala nečitljiv tekst.
        await PokreniDemoAsync();

        var demo = await Query(db => db.Users.SingleAsync(u => u.Email == DemoSeed.KlijentEmail));
        var razgovori = await WithService<ConversationService, List<DTOs.Conversations.ConversationDto>>(
            svc => svc.GetConversationsAsync(demo.Id));

        razgovori.Should().HaveCount(2);
        razgovori.Should().OnlyContain(r => !string.IsNullOrEmpty(r.LastMessagePreview)
                                         && r.LastMessagePreview!.Contains(' '));
    }

    [Fact]
    public async Task DemoSeed_NovcanikDemoNalogaSeSlazeSaIstorijom()
    {
        // Play kadar 7 prikazuje i balans i istoriju — ako se razilaze, to se
        // vidi na snimku. Balans mora biti zbir stavki, a BalanceAfter lanac.
        await PokreniDemoAsync();

        var demo = await Query(db => db.Users.SingleAsync(u => u.Email == DemoSeed.KlijentEmail));
        var stavke = await Query(db => db.TokenTransactions
            .Where(t => t.UserId == demo.Id)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync());

        stavke.Should().HaveCountGreaterThan(5);
        stavke.Select(t => t.Kind).Distinct().Should().HaveCountGreaterThan(3, "raznovrsna istorija za snimak");
        demo.TokenBalance.Should().Be(stavke.Sum(t => t.Amount)).And.BePositive();

        var stanje = 0m;
        foreach (var t in stavke)
        {
            stanje += t.Amount;
            t.BalanceAfter.Should().Be(stanje);
        }

        // Svi korisnici: balans = zbir knjige (seed ne sme da „štampa" tokene).
        var balansi = await Query(db => db.Users
            .Where(u => u.Email!.EndsWith("@" + DemoSeed.Domen))
            .Select(u => new
            {
                u.Email,
                u.TokenBalance,
                Knjiga = db.TokenTransactions.Where(t => t.UserId == u.Id).Sum(t => t.Amount)
            })
            .ToListAsync());
        balansi.Should().OnlyContain(b => b.TokenBalance == b.Knjiga);
    }

    [Fact]
    public async Task DemoSeed_DemoNalogImaSvojeOglaseINeOcenjujeSebe()
    {
        // Play kadar 6 („Moji oglasi") se snima sa istog naloga kao ostali.
        await PokreniDemoAsync();

        var demo = await Query(db => db.Users.SingleAsync(u => u.Email == DemoSeed.KlijentEmail));

        var mojiOglasi = await Query(db => db.Listings
            .Where(l => l.ProviderProfile.UserId == demo.Id)
            .Select(l => l.Id).ToListAsync());
        mojiOglasi.Should().HaveCount(2);

        (await Query(db => db.Reviews.AnyAsync(r => mojiOglasi.Contains(r.ListingId) && r.AuthorId == demo.Id)))
            .Should().BeFalse("niko ne ocenjuje sopstveni oglas");
    }
}
