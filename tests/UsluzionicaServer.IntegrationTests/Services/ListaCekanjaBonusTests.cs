using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Bonus za listu čekanja (Marketing 00, preduslov 7): sajt obećava tokene
/// svakome ko se upisao pre izlaska i otvori nalog istom adresom.
/// </summary>
public class ListaCekanjaBonusTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // Factory postavlja Waitlist:BonusTokens = 7.
    private const decimal Bonus = 7m;

    private static void Upisi(string putanja, string email, DateTimeOffset? kada = null,
                              string platforma = "android", string? lista = null)
    {
        // Isti oblik reda koji piše PrijavaServis na sajtu.
        var red = JsonSerializer.Serialize(new
        {
            kontakt  = email,
            tip      = "email",
            izvor    = "hero",
            platforma,
            lista,
            kreirano = (kada ?? DateTimeOffset.UtcNow.AddDays(-3)).ToString("O", CultureInfo.InvariantCulture)
        });
        File.AppendAllText(putanja, red + Environment.NewLine);
    }

    private void UpisiNaListu(string email, DateTimeOffset? kada = null) =>
        Upisi(Factory.ListaCekanjaPutanja, email, kada);

    private Task<decimal> Balans(string userId) =>
        Query(db => db.Users.Where(u => u.Id == userId).Select(u => u.TokenBalance).SingleAsync());

    private Task<int> BrojBonusa(string userId) =>
        Query(db => db.TokenTransactions.CountAsync(t => t.UserId == userId && t.Kind == TokenKind.WaitlistBonus));

    /// <summary>Servis sa sopstvenom konfiguracijom — za pravila datuma izlaska.</summary>
    private Task<T> SaKonfiguracijom<T>(Dictionary<string, string?> konf, Func<ListaCekanjaBonus, Task<T>> akcija) =>
        WithService<IServiceProvider, T>(async sp =>
        {
            var cfg   = new ConfigurationBuilder().AddInMemoryCollection(konf).Build();
            var lista = new ListaCekanja(cfg, NullLogger<ListaCekanja>.Instance);
            var bonus = new ListaCekanjaBonus(
                sp.GetRequiredService<AppDbContext>(), lista,
                sp.GetRequiredService<NotificationService>(), cfg,
                NullLogger<ListaCekanjaBonus>.Instance);
            return await akcija(bonus);
        });

    private static string NovaLista() =>
        Path.Combine(Path.GetTempPath(), $"usluzionica-lista-{Guid.NewGuid():N}.jsonl");

    // ── Glavni tok ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistracijaIstomAdresom_KlikNaLink_DonosiBonusJednom()
    {
        // Ceo put kao u stvarnosti: upis na sajtu → registracija u aplikaciji →
        // klik na verifikacioni link. Link se klikće DVAPUT (mail klijent ga
        // prefetch-uje), a bonus sme samo jednom.
        const string email = "sa-liste@test.rs";
        UpisiNaListu(email);

        var client = Factory.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Sa Liste", email, password = "MojaLoz123!", acceptedPolicy = true
        });
        reg.StatusCode.Should().Be(HttpStatusCode.OK);

        var link = new Uri(Email.LastVerificationUrlFor(email)!).PathAndQuery;
        (await client.GetAsync(link)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(link)).StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await Query(db => db.Users.SingleAsync(u => u.Email == email));
        (await Balans(user.Id)).Should().Be(Bonus);
        (await BrojBonusa(user.Id)).Should().Be(1);

        var obavestenja = await Query(db => db.Notifications
            .CountAsync(n => n.UserId == user.Id && n.Kind == NotificationKind.TokenEarned));
        obavestenja.Should().Be(1);
    }

    [Fact]
    public async Task AdresaKojeNemaNaListi_NemaBonusa()
    {
        UpisiNaListu("neko-drugi@test.rs");
        var user = await Data.CreateConfirmedUserAsync("nije-na-listi@test.rs");

        (await WithService<ListaCekanjaBonus, bool>(b => b.PokusajAsync(user.Id))).Should().BeFalse();
        (await Balans(user.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task NepotvrdjenaAdresa_NemaBonusa()
    {
        // Inače bi bilo ko otvorio nalog sa tuđom adresom sa liste i uzeo bonus.
        UpisiNaListu("tudja@test.rs");
        var user = await Data.CreateUnconfirmedUserAsync("tudja@test.rs");

        (await WithService<ListaCekanjaBonus, bool>(b => b.PokusajAsync(user.Id))).Should().BeFalse();
        (await Balans(user.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task VelikaSlovaUAdresi_NeSmetaju()
    {
        UpisiNaListu("  Mika.Mikic@Test.RS ");
        var user = await Data.CreateConfirmedUserAsync("mika.mikic@test.rs");

        (await WithService<ListaCekanjaBonus, bool>(b => b.PokusajAsync(user.Id))).Should().BeTrue();
        (await Balans(user.Id)).Should().Be(Bonus);
    }

    // ── Prolaz ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Prolaz_IsplacujeNalogPotvrdjenPreUpisa_IToSamoJednom()
    {
        // Nalog postoji i potvrđen je, a adresa se tek posle pojavi na listi —
        // potvrda je prošla, pa jedino prolaz može da ga uhvati.
        var user = await Data.CreateConfirmedUserAsync("rani-nalog@test.rs");
        (await Balans(user.Id)).Should().Be(0m);

        UpisiNaListu("rani-nalog@test.rs");

        (await WithService<ListaCekanjaBonus, int>(b => b.ProdjiAsync())).Should().BeGreaterThanOrEqualTo(1);
        (await WithService<ListaCekanjaBonus, int>(b => b.ProdjiAsync())).Should().Be(0);

        (await Balans(user.Id)).Should().Be(Bonus);
        (await BrojBonusa(user.Id)).Should().Be(1);
    }

    // ── Pravila sa sajta ───────────────────────────────────────────────────

    private static string D(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    [Fact]
    public async Task UpisPosleIzlaskaZaAndroid_JeIphoneLista_VaziDokIphoneNeIzadje()
    {
        // Posle izlaska za Android sajt nudi preuzimanje, a formu samo za
        // iPhone — takav upis čeka iPhone i nosi isti bonus.
        var putanja = NovaLista();
        var android = DateTimeOffset.UtcNow.AddDays(-5);
        Upisi(putanja, "za-iphone@test.rs", android.AddDays(1), platforma: "desktop");
        var user = await Data.CreateConfirmedUserAsync("za-iphone@test.rs");

        var dobio = await SaKonfiguracijom(new()
        {
            ["Waitlist:Path"]              = putanja,
            ["Waitlist:AndroidLaunchDate"] = D(android)
        }, b => b.PokusajAsync(user.Id));

        dobio.Should().BeTrue();
        (await Balans(user.Id)).Should().Be(10m);   // podrazumevani iznos (ova konfiguracija ga ne menja)
    }

    [Fact]
    public async Task UpisPosleIzlaskaZaIphone_NemaBonusa()
    {
        var putanja = NovaLista();
        var android = DateTimeOffset.UtcNow.AddDays(-30);
        var ios     = DateTimeOffset.UtcNow.AddDays(-5);
        Upisi(putanja, "kasno-iphone@test.rs", ios.AddDays(1), platforma: "ios");
        var user = await Data.CreateConfirmedUserAsync("kasno-iphone@test.rs");

        var dobio = await SaKonfiguracijom(new()
        {
            ["Waitlist:Path"]              = putanja,
            ["Waitlist:AndroidLaunchDate"] = D(android),
            ["Waitlist:IosLaunchDate"]     = D(ios)
        }, b => b.PokusajAsync(user.Id));

        dobio.Should().BeFalse();
    }

    [Fact]
    public async Task IphonePrijavaPreAndroida_NeIsticeSaAndroidRokom()
    {
        // Upisao se sa iPhone-a pre izlaska za Android — Android verziju nije
        // ni mogao da instalira. Android rok od 60 dana je prošao, ali njegov
        // teče tek od izlaska za iPhone.
        var putanja = NovaLista();
        var android = DateTimeOffset.UtcNow.AddDays(-70);
        Upisi(putanja, "rani-iphone@test.rs",  android.AddDays(-10), platforma: "ios");
        Upisi(putanja, "rani-android@test.rs", android.AddDays(-10), platforma: "android");
        var iphone  = await Data.CreateConfirmedUserAsync("rani-iphone@test.rs");
        var androidKorisnik = await Data.CreateConfirmedUserAsync("rani-android@test.rs");

        var konf = new Dictionary<string, string?>
        {
            ["Waitlist:Path"]              = putanja,
            ["Waitlist:AndroidLaunchDate"] = D(android)
        };

        (await SaKonfiguracijom(konf, b => b.PokusajAsync(iphone.Id))).Should().BeTrue();
        (await SaKonfiguracijom(konf, b => b.PokusajAsync(androidKorisnik.Id))).Should().BeFalse();
    }

    [Fact]
    public async Task IzricitaListaSaSajta_ImaPrednost()
    {
        // Sajt posle izlaska za Android upisuje `lista: "ios"`. Čak i ako bi
        // datum i uređaj rekli drugo, važi ono što je sajt upisao.
        var putanja = NovaLista();
        var android = DateTimeOffset.UtcNow.AddDays(-70);
        Upisi(putanja, "izricito@test.rs", android.AddDays(-10), platforma: "android", lista: "ios");
        var user = await Data.CreateConfirmedUserAsync("izricito@test.rs");

        (await SaKonfiguracijom(new()
        {
            ["Waitlist:Path"]              = putanja,
            ["Waitlist:AndroidLaunchDate"] = D(android)
        }, b => b.PokusajAsync(user.Id))).Should().BeTrue();
    }

    [Fact]
    public async Task NalogPosleRoka_NemaBonusa_UrokuIma()
    {
        var putanja = NovaLista();
        var izlazak = DateTimeOffset.UtcNow.AddDays(-61);
        Upisi(putanja, "posle-roka@test.rs", izlazak.AddDays(-10));
        Upisi(putanja, "u-roku@test.rs",     izlazak.AddDays(-10));
        var kasni  = await Data.CreateConfirmedUserAsync("posle-roka@test.rs");
        var tacan  = await Data.CreateConfirmedUserAsync("u-roku@test.rs");

        var konf = new Dictionary<string, string?>
        {
            ["Waitlist:Path"]              = putanja,
            ["Waitlist:AndroidLaunchDate"] = D(izlazak)
        };

        // Rok od 60 dana je istekao juče.
        (await SaKonfiguracijom(konf, b => b.PokusajAsync(kasni.Id))).Should().BeFalse();

        // Isti izlazak, ali rok produžen na 90 dana — sad je u roku.
        konf["Waitlist:ClaimDays"] = "90";
        (await SaKonfiguracijom(konf, b => b.PokusajAsync(tacan.Id))).Should().BeTrue();
    }

    [Fact]
    public async Task AdresaKojaJeVecDobilaBonus_NeDobijaPonovo_NiNovimNalogom()
    {
        // Stari nalog obrisan, nov otvoren istom adresom: red o isplati ostaje
        // (bez stranog ključa, sa hešom adrese), pa drugi bonus ne prolazi.
        UpisiNaListu("ponovo@test.rs");
        await Query(async db =>
        {
            db.WaitlistBonuses.Add(new WaitlistBonus
            {
                EmailHash  = ListaCekanja.Hes("ponovo@test.rs"),
                UserId     = "obrisan-nalog",
                Amount     = Bonus,
                SignedUpAt = DateTime.UtcNow.AddDays(-30)
            });
            return await db.SaveChangesAsync();
        });

        var nov = await Data.CreateConfirmedUserAsync("ponovo@test.rs");

        (await WithService<ListaCekanjaBonus, bool>(b => b.PokusajAsync(nov.Id))).Should().BeFalse();
        (await Balans(nov.Id)).Should().Be(0m);
    }

    [Fact]
    public async Task BezPutanje_BonusJeIskljucen()
    {
        var user = await Data.CreateConfirmedUserAsync("bez-liste@test.rs");

        var dobio = await SaKonfiguracijom(new() { ["Waitlist:Path"] = "" }, b => b.PokusajAsync(user.Id));
        dobio.Should().BeFalse();
    }
}
