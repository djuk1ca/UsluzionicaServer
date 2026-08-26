using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravila aktivacije provajder naloga.
///
/// Aktivacija je jedini prelaz iz "korisnik" u "korisnik koji nudi usluge", pa
/// se ovde spajaju validacija ulaza (grad, kategorije), stanje naloga
/// (potvrđen email) i posledice po ostatak sistema (IsProvider, referral).
///
/// Pravilo "jedan korisnik = jedan profil" ima DVA sloja: proveru u kodu i
/// unique constraint u bazi. Test proverava kod; constraint je poslednja
/// odbrana ako kod nekad zakaže pod paralelnim zahtevima.
/// </summary>
public class ProviderActivationTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static ActivateProviderDto Zahtev(
        string  profession  = "Vodoinstalater",
        string? location    = null,
        List<int>? kategorije = null) => new()
    {
        Profession  = profession,
        Location    = location ?? TestData.ValidCity,
        CategoryIds = kategorije ?? [TestData.SeededCategoryId]
    };

    private Task<(ProviderProfileDto? Profil, string? Greska)> AktivirajAsync(
        string userId, ActivateProviderDto? dto = null)
        => WithService<ProviderService, (ProviderProfileDto?, string?)>(
            svc => svc.ActivateAsync(userId, dto ?? Zahtev()));

    // ── 1. Jedan korisnik = jedan profil ───────────────────────────────────

    [Fact]
    public async Task Aktivacija_KadaProfilVecPostoji_Odbija()
    {
        // Dva profila za isti nalog bi razdvojila oglase, recenzije i prosečnu
        // ocenu na dva mesta — a korisnik bi u aplikaciji video samo jedan.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");
        await AktivirajAsync(korisnik.Id);

        // Act
        var (profil, greska) = await AktivirajAsync(korisnik.Id, Zahtev(profession: "Moler"));

        // Assert
        profil.Should().BeNull();
        greska.Should().Contain("već postoji");

        (await Query(db => db.ProviderProfiles.CountAsync(p => p.UserId == korisnik.Id)))
            .Should().Be(1);

        // Neuspela aktivacija ne sme prepisati podatke prve.
        (await Query(db => db.ProviderProfiles.Where(p => p.UserId == korisnik.Id)
            .Select(p => p.Profession).FirstAsync()))
            .Should().Be("Vodoinstalater");
    }

    [Fact]
    public async Task Aktivacija_KadaProfilNePostoji_Prolazi()
    {
        // POZITIVAN par. Bez njega bi i kod koji odbija svaku aktivaciju prošao
        // test iznad.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");

        // Act
        var (profil, greska) = await AktivirajAsync(korisnik.Id);

        // Assert
        profil.Should().NotBeNull(greska);
        profil!.Profession.Should().Be("Vodoinstalater");
        profil.Location.Should().Be(TestData.ValidCity);
        (await Query(db => db.ProviderProfiles.CountAsync())).Should().Be(1);
    }

    // ── 2. Nepotvrđen email ────────────────────────────────────────────────

    [Fact]
    public async Task Aktivacija_KadaEmailNijePotvrdjen_OdbijaINeOstavljaTragove()
    {
        // Provajder je javna uloga — njegovo ime i grad vide svi. Bez potvrđene
        // adrese nema načina da se kontaktira niti da se nalog povrati.

        // Arrange
        var nepotvrdjeni = await Data.CreateUnconfirmedUserAsync("nepotvrdjen@test.rs");

        // Act
        var (profil, greska) = await AktivirajAsync(nepotvrdjeni.Id);

        // Assert
        profil.Should().BeNull();
        greska.Should().Contain("email");

        // Neuspeh mora biti POTPUN — ni profil, ni veze ka kategorijama,
        // ni zastavica na korisniku.
        (await Query(db => db.ProviderProfiles.CountAsync())).Should().Be(0);
        (await Query(db => db.ProviderCategories.CountAsync())).Should().Be(0);
        (await Query(db => db.Users.Where(u => u.Id == nepotvrdjeni.Id)
            .Select(u => u.IsProvider).FirstAsync())).Should().BeFalse();
    }

    // ── 3. IsProvider se postavlja ─────────────────────────────────────────

    [Fact]
    public async Task Aktivacija_KadaUspe_PostavljaIsProviderNaKorisniku()
    {
        // IsProvider je denormalizovana kopija činjenice "postoji ProviderProfile".
        // Postoji zato što se čita u svakom JWT-u i na svakom ekranu, pa se ne
        // isplati join. Cena je što može da se razmimoiđe sa stvarnošću —
        // otuda test.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");

        (await Query(db => db.Users.Where(u => u.Id == korisnik.Id)
            .Select(u => u.IsProvider).FirstAsync()))
            .Should().BeFalse("priprema: korisnik još nije provajder");

        // Act
        await AktivirajAsync(korisnik.Id);

        // Assert
        (await Query(db => db.Users.Where(u => u.Id == korisnik.Id)
            .Select(u => u.IsProvider).FirstAsync()))
            .Should().BeTrue();

        // Zastavica i profil moraju se slagati — to je celo pravilo.
        (await Query(db => db.ProviderProfiles.AnyAsync(p => p.UserId == korisnik.Id)))
            .Should().BeTrue();
    }

    // ── 4. Nevalidan grad ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Beogradd")]      // greška u kucanju
    [InlineData("Zagreb")]        // van Srbije
    [InlineData("")]
    [InlineData("Novi  Sad")]     // dvostruki razmak
    [InlineData("subotica")]      // mala slova — lista je case-sensitive
    [InlineData("Beograd")]       // vidi napomenu ispod — NIJE validan unos
    public async Task Aktivacija_KadaGradNijeSaZvanicneListe_Odbija(string grad)
    {
        // Grad je polje po kom se filtrira pretraga. Da su dozvoljeni slobodni
        // unosi, "Beograd", "beograd" i "Beogr." bili bi tri različita grada i
        // filter bi tiho gubio oglase.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");

        // Act
        var (profil, greska) = await AktivirajAsync(korisnik.Id, Zahtev(location: grad));

        // Assert
        profil.Should().BeNull();
        greska.Should().Contain("nije prepoznata opština");
        (await Query(db => db.ProviderProfiles.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData("Subotica")]
    [InlineData("Novi Sad")]
    [InlineData("Čačak")]              // dijakritika mora proći netaknuta
    [InlineData("Beograd — Vračar")]   // Beograd postoji SAMO po opštinama
    public async Task Aktivacija_KadaJeGradSaZvanicneListe_Prolazi(string grad)
    {
        // POZITIVAN par: dokazuje da lista zaista sadrži prave gradove, a ne
        // da validacija odbija baš sve.
        //
        // NAPOMENA O BEOGRADU — otkriveno pisanjem ovog testa:
        // u listi ne postoji unos "Beograd", nego 17 zasebnih opština oblika
        // "Beograd — Vračar" (sa dugom crtom, ne crticom). Znači da korisnik
        // koji ukuca "Beograd" — najverovatniji unos u celoj Srbiji — dobija
        // grešku da grad nije prepoznat.
        //
        // To NIJE greška u kodu; lista je namerno po opštinama. Ali jeste rupa
        // u korisničkom iskustvu ako klijent negde dozvoljava slobodan unos
        // umesto biranja iz liste. Zapisano testom (i u negativnom Theory
        // bloku iznad) da ostane vidljivo.

        // Arrange — baza se resetuje pre SVAKOG testa (IntegrationTestBase),
        // pa svaki Theory slučaj kreće od prazne baze i email sme biti isti.
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");

        // Act
        var (profil, greska) = await AktivirajAsync(korisnik.Id, Zahtev(location: grad));

        // Assert
        profil.Should().NotBeNull(greska);
        profil!.Location.Should().Be(grad);
    }

    // ── 5. Kategorije se vezuju ────────────────────────────────────────────

    [Fact]
    public async Task Aktivacija_SaViseKategorija_VezujeSveBezDuplikata()
    {
        // Kategorije određuju gde se provajder pojavljuje u pretrazi. Duplikat
        // u ulazu bi napravio dva reda sa istim composite ključem — i pao bi
        // na constraint-u umesto da bude tiho očišćen.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");
        var kategorije = await Query(db => db.Categories
            .Where(c => c.ParentId != null)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .Take(3)
            .ToListAsync());

        kategorije.Should().HaveCount(3, "seed mora imati bar 3 podkategorije");

        // Namerno se prva kategorija šalje dvaput.
        var saDuplikatom = new List<int> { kategorije[0], kategorije[1], kategorije[0], kategorije[2] };

        // Act
        var (profil, greska) = await AktivirajAsync(korisnik.Id, Zahtev(kategorije: saDuplikatom));

        // Assert
        profil.Should().NotBeNull(greska);
        profil!.Categories.Should().HaveCount(3);
        profil.Categories.Select(c => c.CategoryId).Should().BeEquivalentTo(kategorije);

        // Veze moraju stvarno biti u bazi, ne samo u vraćenom DTO-u.
        (await Query(db => db.ProviderCategories
            .CountAsync(pc => pc.ProviderProfileId == profil.ProviderProfileId)))
            .Should().Be(3);

        // Ime kategorije se popunjava iz baze — prazno bi značilo pokvaren join.
        profil.Categories.Should().OnlyContain(c => c.CategoryName != "");
    }

    [Fact]
    public async Task Aktivacija_SaNepostojecomKategorijom_OdbijaIImenujeJe()
    {
        // Greška mora da kaže KOJA kategorija ne postoji. Poruka "nevalidan
        // unos" bi klijentu ostavila da pogađa koju je od deset poslao pogrešno.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");

        // Act
        var (profil, greska) = await AktivirajAsync(
            korisnik.Id, Zahtev(kategorije: [TestData.SeededCategoryId, 999_999]));

        // Assert
        profil.Should().BeNull();
        greska.Should().Contain("999999");

        (await Query(db => db.ProviderProfiles.CountAsync())).Should().Be(0);
        (await Query(db => db.ProviderCategories.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Aktivacija_SaViseOdDesetKategorija_Odbija()
    {
        // Gornja granica postoji da provajder ne bi "pokrio sve" i pojavljivao
        // se u svakoj pretrazi bez veze sa onim što stvarno radi.

        // Arrange
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");
        var jedanaest = await Query(db => db.Categories
            .Where(c => c.ParentId != null)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .Take(11)
            .ToListAsync());

        // Act
        var (profil, greska) = await AktivirajAsync(korisnik.Id, Zahtev(kategorije: jedanaest));

        // Assert
        profil.Should().BeNull();
        greska.Should().Contain("maksimalno 10");
        (await Query(db => db.ProviderProfiles.CountAsync())).Should().Be(0);
    }
}
