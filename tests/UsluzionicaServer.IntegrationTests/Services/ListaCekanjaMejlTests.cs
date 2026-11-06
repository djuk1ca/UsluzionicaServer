using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Mejl „Uslužionica je na App Store-u" listi čekanja (ListaCekanjaMejl).
///
/// Sve „slanje" ide kroz FakeEmailService — nijedan test ne otvara SMTP.
/// Najvažnije osobine: dry-run je podrazumevan, nikome dvaput, ko ima nalog
/// ne dobija poruku, i radnja je samo za admina.
/// </summary>
public class ListaCekanjaMejlTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const string AppStore = "https://apps.apple.com/test/usluzionica";

    // ── Pomoćno ────────────────────────────────────────────────────────────

    private static string NovaLista() =>
        Path.Combine(Path.GetTempPath(), $"usluzionica-lista-{Guid.NewGuid():N}.jsonl");

    private static void Upisi(string putanja, string email, DateTimeOffset? kada = null,
                              string platforma = "ios", string? lista = null)
    {
        // Isti oblik reda koji piše PrijavaServis na sajtu.
        var red = JsonSerializer.Serialize(new
        {
            kontakt  = email,
            tip      = "email",
            izvor    = "hero",
            platforma,
            lista,
            kreirano = (kada ?? DateTimeOffset.UtcNow.AddDays(-10)).ToString("O", CultureInfo.InvariantCulture)
        });
        File.AppendAllText(putanja, red + Environment.NewLine);
    }

    private static string D(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Konfiguracija u kojoj je iPhone izašao juče i App Store link je upisan.</summary>
    private static Dictionary<string, string?> Izasao(string putanja, DateTimeOffset? ios = null) => new()
    {
        ["Waitlist:Path"]          = putanja,
        ["Waitlist:IosLaunchDate"] = D(ios ?? DateTimeOffset.UtcNow.AddDays(-1)),
        ["App:AppStoreUrl"]        = AppStore
    };

    /// <summary>
    /// Servis sa konfiguracijom testa POVRH konfiguracije hosta — za pravila
    /// datuma izlaska, kao u ListaCekanjaBonusTests. Email je isti FakeEmailService.
    /// </summary>
    private Task<T> Mejl<T>(Dictionary<string, string?> konf, Func<ListaCekanjaMejl, Task<T>> akcija) =>
        WithService<IServiceProvider, T>(async sp =>
        {
            var cfg = new ConfigurationBuilder()
                .AddConfiguration(sp.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(konf)
                .Build();
            var db    = sp.GetRequiredService<AppDbContext>();
            var lista = new ListaCekanja(cfg, NullLogger<ListaCekanja>.Instance);
            var bonus = new ListaCekanjaBonus(db, lista, sp.GetRequiredService<NotificationService>(), cfg,
                                              NullLogger<ListaCekanjaBonus>.Instance);
            var mejl  = new ListaCekanjaMejl(db, lista, bonus, sp.GetRequiredService<IEmailService>(),
                                             sp.GetRequiredService<DistributedLock>(), cfg,
                                             NullLogger<ListaCekanjaMejl>.Instance);
            return await akcija(mejl);
        });

    private Task<ListaCekanjaMejl.Izvestaj> Posalji(Dictionary<string, string?> konf, int? max = null,
                                                    bool ponoviNeuspele = false) =>
        Mejl(konf, m => m.PokreniAsync(new ListaCekanjaMejl.Opcije(Posalji: true, Max: max, PonoviNeuspele: ponoviNeuspele)));

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

    private async Task<HttpClient> AdminAsync()
    {
        var admin = await Data.CreateAdminAsync();
        return await PrijavljenAsync(admin.Email!);
    }

    private string[] Primaoci() => Email.ListaCekanja.Select(m => m.To).OrderBy(x => x).ToArray();

    // ── Dry-run ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_JePodrazumevan_NistaNeSalje_IVracaBrojIPregledBezAdresa()
    {
        // Factory lista (Waitlist:Path) — endpoint radi sa pravom konfiguracijom hosta.
        Upisi(Factory.ListaCekanjaPutanja, "prva@lista.rs");
        Upisi(Factory.ListaCekanjaPutanja, "druga@lista.rs", platforma: "android");
        var admin = await AdminAsync();

        var odgovor = await admin.PostAsync("/api/admin/waitlist/launch-email", null);
        odgovor.StatusCode.Should().Be(HttpStatusCode.OK);
        var telo = await odgovor.Content.ReadAsStringAsync();

        Email.ListaCekanja.Should().BeEmpty();
        (await Query(db => db.WaitlistEmails.CountAsync())).Should().Be(0);

        var data = JsonDocument.Parse(telo).RootElement.GetProperty("data");
        data.GetProperty("posalji").GetBoolean().Should().BeFalse();
        data.GetProperty("primalaca").GetInt32().Should().Be(2);
        data.GetProperty("poslato").GetInt32().Should().Be(0);

        var html = data.GetProperty("pregledSaBonusom").GetProperty("html").GetString();
        html.Should().Contain("alt=\"Preuzmi u App Storeu\"")
            .And.Contain(ListaCekanjaMejlSablon.AppStorePlaceholder)   // link još nije upisan
            .And.Contain("Odjavi se");

        // Nijedna adresa sa liste ne izlazi iz servera.
        telo.Should().NotContain("prva@lista.rs").And.NotContain("druga@lista.rs");

        data.GetProperty("upozorenja").EnumerateArray().Select(u => u.GetString())
            .Should().Contain(u => u!.Contains("App:AppStoreUrl"));
    }

    [Fact]
    public async Task PravoSlanje_PreIzlaskaIBezLinka_JeOdbijeno()
    {
        // Host nema ni Waitlist:IosLaunchDate ni App:AppStoreUrl.
        Upisi(Factory.ListaCekanjaPutanja, "ceka@lista.rs");
        var admin = await AdminAsync();

        var odgovor = await admin.PostAsync("/api/admin/waitlist/launch-email?posalji=true", null);

        odgovor.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Email.ListaCekanja.Should().BeEmpty();
        (await Query(db => db.WaitlistEmails.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task PravoSlanje_SaDatumomUBuducnosti_JeOdbijeno()
    {
        var putanja = NovaLista();
        Upisi(putanja, "rano@lista.rs");

        var akcija = () => Posalji(Izasao(putanja, ios: DateTimeOffset.UtcNow.AddDays(3)));

        await akcija.Should().ThrowAsync<ListaCekanjaMejl.Odbijeno>();
        Email.ListaCekanja.Should().BeEmpty();
    }

    // ── Samo admin ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SamoAdmin_ObicanKorisnikIGost_NemajuPristup()
    {
        Upisi(Factory.ListaCekanjaPutanja, "neko@lista.rs");
        await Data.CreateConfirmedUserAsync("obican@test.rs");
        var korisnik = await PrijavljenAsync("obican@test.rs");
        var gost     = Factory.CreateClient();

        (await korisnik.PostAsync("/api/admin/waitlist/launch-email?posalji=true", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await gost.PostAsync("/api/admin/waitlist/launch-email?posalji=true", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await korisnik.PostAsJsonAsync("/api/admin/waitlist/launch-email/test", new { email = "obican@test.rs" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        Email.ListaCekanja.Should().BeEmpty();
    }

    // ── Nikome dvaput ──────────────────────────────────────────────────────

    [Fact]
    public async Task PravoSlanje_SvakomJednom_PonovnoPokretanjeNistaNeSalje()
    {
        var putanja = NovaLista();
        var adrese = Enumerable.Range(1, 5).Select(i => $"osoba{i}@lista.rs").ToArray();
        foreach (var a in adrese) Upisi(putanja, a);

        // Paket je 2 (factory) — pet adresa ide u tri paketa.
        var prvo = await Posalji(Izasao(putanja));
        prvo.Poslato.Should().Be(5);
        prvo.Preostalo.Should().Be(0);
        Primaoci().Should().Equal(adrese.OrderBy(x => x));

        var drugo = await Posalji(Izasao(putanja));
        drugo.Poslato.Should().Be(0);
        drugo.Primalaca.Should().Be(0);
        drugo.Preskoceno.VecPoslato.Should().Be(5);

        Email.ListaCekanja.Should().HaveCount(5);
        (await Query(db => db.WaitlistEmails.CountAsync(m => m.SentAt != null))).Should().Be(5);
    }

    [Fact]
    public async Task DvaIstovremenaSlanja_NeSaljuDuplikat()
    {
        var putanja = NovaLista();
        var adrese = Enumerable.Range(1, 8).Select(i => $"paralelno{i}@lista.rs").ToArray();
        foreach (var a in adrese) Upisi(putanja, a);

        // Ako je Redis dostupan, drugi zahtev odbije brava („slanje već traje");
        // bez Redis-a oba krenu, i tada duplikat sprečava jedinstveni indeks.
        async Task Jedno()
        {
            try { await Posalji(Izasao(putanja)); }
            catch (ListaCekanjaMejl.Odbijeno) { }
        }

        await Task.WhenAll(Jedno(), Jedno());

        Primaoci().Should().OnlyHaveUniqueItems().And.HaveCount(8);
    }

    [Fact]
    public async Task Max_OgranicavaJednoPokretanje_SledeceNastavlja()
    {
        var putanja = NovaLista();
        for (var i = 1; i <= 5; i++) Upisi(putanja, $"red{i}@lista.rs", DateTimeOffset.UtcNow.AddDays(-20 + i));

        var prvo = await Posalji(Izasao(putanja), max: 2);
        prvo.Poslato.Should().Be(2);
        prvo.Preostalo.Should().Be(3);

        // Najstariji upisi idu prvi — čekali su najduže.
        Primaoci().Should().Equal("red1@lista.rs", "red2@lista.rs");

        var drugo = await Posalji(Izasao(putanja), max: 10);
        drugo.Poslato.Should().Be(3);
        Primaoci().Should().HaveCount(5).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task NeuspeloSlanje_NePonavljaSeSamo_SamoNaIzricitZahtev()
    {
        var putanja = NovaLista();
        Upisi(putanja, "uspeh@lista.rs");
        Upisi(putanja, "pada@lista.rs");
        Email.PadniZa["pada@lista.rs"] = true;

        var prvo = await Posalji(Izasao(putanja));
        prvo.Poslato.Should().Be(1);
        prvo.Neuspelo.Should().Be(1);

        // SMTP je možda ipak primio poruku — bez izričitog zahteva nema ponavljanja.
        Email.PadniZa.Clear();
        var drugo = await Posalji(Izasao(putanja));
        drugo.Poslato.Should().Be(0);
        drugo.Preskoceno.NeuspeloRanije.Should().Be(1);

        var trece = await Posalji(Izasao(putanja), ponoviNeuspele: true);
        trece.Poslato.Should().Be(1);
        Primaoci().Should().Equal("pada@lista.rs", "uspeh@lista.rs");
    }

    // ── Ko ne dobija poruku ────────────────────────────────────────────────

    [Fact]
    public async Task PostojeciNalog_SePreskace_IPotvrdjenINepotvrdjen()
    {
        var putanja = NovaLista();
        Upisi(putanja, "ima-nalog@lista.rs");
        Upisi(putanja, "Nepotvrdjen@Lista.rs");
        Upisi(putanja, "nema-nalog@lista.rs");
        await Data.CreateConfirmedUserAsync("ima-nalog@lista.rs");
        await Data.CreateUnconfirmedUserAsync("nepotvrdjen@lista.rs");

        var izvestaj = await Posalji(Izasao(putanja));

        izvestaj.Preskoceno.ImaNalog.Should().Be(2);
        Primaoci().Should().Equal("nema-nalog@lista.rs");
    }

    [Fact]
    public async Task ObrisanNalogKojiJeDobioBonus_SePreskace()
    {
        // Adresa je bonus već dobila pa obrisala nalog — trag ostaje u WaitlistBonus.
        var putanja = NovaLista();
        Upisi(putanja, "otisao@lista.rs");
        await Query(async db =>
        {
            db.WaitlistBonuses.Add(new WaitlistBonus
            {
                EmailHash = ListaCekanja.Hes("otisao@lista.rs"), UserId = Guid.NewGuid().ToString(),
                Amount = 7, SignedUpAt = DateTime.UtcNow.AddDays(-30)
            });
            return await db.SaveChangesAsync();
        });

        var izvestaj = await Posalji(Izasao(putanja));

        izvestaj.Preskoceno.ImaNalog.Should().Be(1);
        Email.ListaCekanja.Should().BeEmpty();
    }

    [Fact]
    public async Task UpisPosleIzlaskaNaIphone_SePreskace()
    {
        var putanja = NovaLista();
        var ios = DateTimeOffset.UtcNow.AddDays(-5);
        Upisi(putanja, "pre@lista.rs",   ios.AddDays(-1));
        Upisi(putanja, "posle@lista.rs", ios.AddDays(1));

        var izvestaj = await Posalji(Izasao(putanja, ios));

        izvestaj.Preskoceno.UpisPosleIzlaska.Should().Be(1);
        Primaoci().Should().Equal("pre@lista.rs");
    }

    // ── Sadržaj ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bonus_PominjeSeSamoKadJosVazi_SaTacnimRokom()
    {
        var putanja = NovaLista();
        var android = DateTimeOffset.UtcNow.AddDays(-100);   // Android rok (60 dana) je istekao
        var ios     = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);
        Upisi(putanja, "iphone@lista.rs",  android.AddDays(-5), platforma: "ios");
        Upisi(putanja, "android@lista.rs", android.AddDays(-5), platforma: "android");

        var konf = Izasao(putanja, ios);
        konf["Waitlist:AndroidLaunchDate"] = D(android);
        var izvestaj = await Posalji(konf);

        izvestaj.SaBonusom.Should().Be(1);
        izvestaj.BezBonusa.Should().Be(1);

        var saBonusom = Email.ListaCekanja.Single(m => m.To == "iphone@lista.rs");
        var bezBonusa = Email.ListaCekanja.Single(m => m.To == "android@lista.rs");

        // Factory: Waitlist:BonusTokens = 7, Referral 2 i 3.
        var rok = ListaCekanjaMejlSablon.Datum(ios.UtcDateTime.AddDays(60));
        saBonusom.Subject.Should().Contain("7 tokena");
        saBonusom.Text.Should().Contain("na startu dobijaš 7 tokena")
            .And.Contain("iphone@lista.rs")
            .And.Contain($"do {rok}")
            .And.Contain(AppStore);
        saBonusom.Html.Should().Contain("alt=\"Preuzmi u App Storeu\"").And.Contain(AppStore);

        bezBonusa.Subject.Should().NotContain("tokena");
        bezBonusa.Text.Should().NotContain("na startu dobijaš");

        foreach (var m in new[] { saBonusom, bezBonusa })
        {
            m.Text.Should().Contain("Profil → Pozovi prijatelje")
                .And.Contain("dobijaš 2 tokena, a još 3 kad postane uslugodavac")
                .And.Contain("play.google.com");
            m.OdjavaUrl.Should().StartWith("https://test.usluzionica.rs/api/lista-cekanja/odjava?");
            m.Html.Should().Contain(WebUtility.HtmlEncode(m.OdjavaUrl));
        }
    }

    // ── Odjava ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Odjava_LinkIzMejla_IskljucujeAdresuIzSlanja()
    {
        var putanja = NovaLista();
        Upisi(putanja, "dosta@lista.rs");
        Upisi(putanja, "ostaje@lista.rs");

        var link = await Mejl(Izasao(putanja), m => Task.FromResult(m.OdjavaUrl(ListaCekanja.Hes("dosta@lista.rs"))));
        var putIUpit = new Uri(link).PathAndQuery;
        var gost = Factory.CreateClient();

        // Klik iz mejla (GET) pa i Gmail-ova odjava jednim klikom (POST) — oba idempotentna.
        var strana = await gost.GetAsync(putIUpit);
        strana.StatusCode.Should().Be(HttpStatusCode.OK);
        (await strana.Content.ReadAsStringAsync()).Should().Contain("Odjavljen");
        (await gost.PostAsync(putIUpit, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Query(db => db.WaitlistOptOuts.CountAsync())).Should().Be(1);

        var izvestaj = await Posalji(Izasao(putanja));
        izvestaj.Preskoceno.Odjavljeni.Should().Be(1);
        Primaoci().Should().Equal("ostaje@lista.rs");
    }

    [Fact]
    public async Task Odjava_IzmenjenPotpis_NistaNeUpisuje()
    {
        var hes  = ListaCekanja.Hes("tudja@lista.rs");
        var gost = Factory.CreateClient();

        var odgovor = await gost.PostAsync($"/api/lista-cekanja/odjava?a={hes}&p={new string('0', 32)}", null);

        odgovor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Query(db => db.WaitlistOptOuts.CountAsync())).Should().Be(0);
    }

    // ── Proba ──────────────────────────────────────────────────────────────

    /// <summary>Host sa upisanim App Store linkom — za probu preko API-ja, kao iz Admin → Alati.</summary>
    private WebApplicationFactory<Program> HostSaLinkom(string link) =>
        Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["App:AppStoreUrl"] = link })));

    private async Task<HttpClient> AdminNa(WebApplicationFactory<Program> host)
    {
        var admin = await Data.CreateAdminAsync();
        var token = await WithService<TokenService, string>(async t =>
        {
            using var scope = Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            return t.GenerateAccessToken(admin, await users.GetRolesAsync(admin));
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Proba_IdeSamoNaNavedenuAdresu_INeBeleziSeKaoPoslato()
    {
        Upisi(Factory.ListaCekanjaPutanja, "vlasnik@lista.rs");
        Upisi(Factory.ListaCekanjaPutanja, "drugi@lista.rs");
        await using var host = HostSaLinkom(AppStore);
        var admin = await AdminNa(host);

        // Isti oblik zahteva i odgovora kao Admin → Alati → „Pošalji probu".
        var odgovor = await admin.PostAsJsonAsync("/api/admin/waitlist/launch-email/test",
                                                  new { email = "vlasnik@lista.rs", saBonusom = true });

        odgovor.StatusCode.Should().Be(HttpStatusCode.OK);
        var naslov = JsonDocument.Parse(await odgovor.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("naslov").GetString();
        naslov.Should().StartWith("[PROBA]");

        Primaoci().Should().Equal("vlasnik@lista.rs");
        var mejl = Email.ListaCekanja.Single();
        mejl.Subject.Should().Be(naslov);
        mejl.Html.Should().Contain($"href=\"{AppStore}\"").And.NotContain("POPUNI");
        (await Query(db => db.WaitlistEmails.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Proba_BezAppStoreLinka_JeOdbijena_SaJasnomPorukom()
    {
        // Host nema App:AppStoreUrl — ranije je proba tiho slala placeholder,
        // pa je dugme vodilo na „Cannot Connect".
        var admin = await AdminAsync();

        var odgovor = await admin.PostAsJsonAsync("/api/admin/waitlist/launch-email/test",
                                                  new { email = "vlasnik@lista.rs" });

        odgovor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await odgovor.Content.ReadAsStringAsync()).Should().Contain("APP_STORE_URL").And.Contain("App__AppStoreUrl");
        Email.ListaCekanja.Should().BeEmpty();
    }

    [Fact]
    public async Task Proba_NeispravanLink_JeOdbijena()
    {
        var akcija = () => Mejl(new() { ["App:AppStoreUrl"] = "apps.apple.com/bez-seme" },
                                m => m.ProbaAsync("vlasnik@lista.rs"));

        (await akcija.Should().ThrowAsync<ListaCekanjaMejl.Odbijeno>())
            .WithMessage("*nije ispravan link*");
        Email.ListaCekanja.Should().BeEmpty();
    }

    // ── Izgled: bedž i slike ───────────────────────────────────────────────

    [Fact]
    public async Task LinkIzKonfiguracije_ZavrsiUHrefBedza_BezDvostrukogKodiranja()
    {
        // Pravi App Store linkovi imaju ž u putanji i & u upitu.
        const string link = "https://apps.apple.com/rs/app/uslužionica/id1234567890?l=sr&mt=8";
        var putanja = NovaLista();
        Upisi(putanja, "bedz@lista.rs");
        var konf = Izasao(putanja);
        konf["App:AppStoreUrl"] = link;

        await Posalji(konf);

        var html = Email.ListaCekanja.Single().Html;
        html.Should().Contain("<a href=\"https://apps.apple.com/rs/app/uslu%C5%BEionica/id1234567890?l=sr&amp;mt=8\"")
            .And.NotContain("&amp;amp;")
            .And.NotContain("POPUNI");
        html.Should().Contain("src=\"https://test.usluzionica.rs/mejl/app-store-hr.png?v=2\"")
            .And.Contain("src=\"https://test.usluzionica.rs/mejl/logo.png?v=2\"")
            .And.Contain("name=\"color-scheme\" content=\"light dark\"")
            .And.Contain("prefers-color-scheme: dark");
    }

    [Theory]
    [InlineData("/mejl/app-store-hr.png")]
    [InlineData("/mejl/logo.png")]
    public async Task SlikeMejla_SeServirajuSaApija(string put)
    {
        // Gmail ih vuče sa {App:BaseUrl}/mejl/… — moraju proći kroz UseStaticFiles.
        var odgovor = await Factory.CreateClient().GetAsync(put);

        odgovor.StatusCode.Should().Be(HttpStatusCode.OK);
        odgovor.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await odgovor.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void Sablon_BezUzvicnikaIEmodzija_PoGlasuBrenda()
    {
        var p = new ListaCekanjaMejlSablon.Podaci("a@b.rs", new DateTime(2026, 11, 6), AppStore, null,
            10m, new DateTime(2027, 1, 5), 2m, 3m, "https://x/odjava", "https://x/mejl");
        var tekst = ListaCekanjaMejlSablon.Tekst(p) + ListaCekanjaMejlSablon.Naslov(p);

        tekst.Should().NotContain("!");
        tekst.EnumerateRunes().Should().NotContain(r => r.Value >= 0x1F000);
    }
}
