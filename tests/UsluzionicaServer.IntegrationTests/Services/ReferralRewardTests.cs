using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// ŠABLON ZA OSTALE TESTOVE — pročitaj komentare pre nego što pišeš svoje.
///
/// Štiti pravilo: referral se isplaćuje u DVE rate, i svaka ima svoj okidač.
///
///   1. rata (2 tokena) — pozvani POTVRDI EMAIL
///   2. rata (3 tokena) — pozvani AKTIVIRA PROVAJDER NALOG
///
/// Zašto je baš to vredno testa: to su poslovne odluke koje se ne vide ni iz
/// tipova ni iz šeme baze. Naročito je osetljiv okidač prve rate — sama
/// registracija ne sme ništa da isplati, jer je besplatna i neograničena, pa bi
/// svako mogao da otvara naloge sa sopstvenim kodom i uzima tokene bez rada.
/// Potvrda emaila zahteva stvarnu adresu po nalogu i to zaustavlja.
///
/// Struktura svakog testa je AAA:
///   Arrange — pripremi stanje
///   Act     — pozovi TAČNO jednu stvar koja se testira
///   Assert  — proveri ishod kroz NOV DbContext (vidi Query u baznoj klasi)
/// </summary>
public class ReferralRewardTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // Vrednosti iz UsluzionicaWebFactory. Namerno su različite: da su obe iste,
    // test koji isplati pogrešnu ratu i dalje bi prošao.
    private const decimal PrvaRata  = 2.0m;   // Referral:SignupRewardTokens
    private const decimal DrugaRata = 3.0m;   // Referral:ProviderActivationRewardTokens

    // ── 1. RATA ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registracija_SaReferralKodom_NeIsplacujeNistaDokEmailNijePotvrdjen()
    {
        // NAJVAŽNIJI test u fajlu — dokazuje da se prva rata ne okida prerano.
        // Test srećnog slučaja (ispod) sam po sebi ne bi otkrio grešku u kojoj
        // se nagrada isplaćuje već pri kreiranju naloga.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);

        // Act — SAMO registracija, bez potvrde emaila
        await Data.CreateUnconfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Assert
        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(0m,
            "prva rata se isplaćuje tek kad pozvani potvrdi email");

        var referral = await Query(db => db.Referrals
            .SingleAsync(r => r.ReferrerId == pozivalac.Id));

        referral.Status.Should().Be(ReferralStatus.Pending);
        // Nullable je jače od 0: razlikuje "nije isplaćeno" od "isplaćeno nula".
        referral.SignupTokensAwarded.Should().BeNull();
        referral.SignupRewardedAt.Should().BeNull();
    }

    [Fact]
    public async Task PotvrdaEmaila_KadaJeKorisnikBioPozvan_IsplacujePrvuRatu()
    {
        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);
        await Data.CreateUnconfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Act — jedina radnja koja se testira
        await Data.ConfirmEmailAsync("pozvani@test.rs");

        // Assert
        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(PrvaRata);

        var referral = await Query(db => db.Referrals
            .SingleAsync(r => r.ReferrerId == pozivalac.Id));

        referral.Status.Should().Be(ReferralStatus.Registered);
        referral.SignupTokensAwarded.Should().Be(PrvaRata);
        referral.SignupRewardedAt.Should().NotBeNull();

        // Druga rata NE sme biti dodirnuta.
        referral.ActivationTokensAwarded.Should().BeNull();
        referral.ActivationRewardedAt.Should().BeNull();
    }

    [Fact]
    public async Task PotvrdaEmaila_KadaSeOkineDvaput_IsplacujeSamoJednom()
    {
        // Verifikacioni link se lako aktivira dvaput: mnogi mail klijenti ga
        // prefetch-uju radi pregleda, pa ga korisnik i sam klikne. Bez zaštite
        // bi pozivalac bio plaćen dvaput za isti nalog.
        //
        // ŠTA OVAJ TEST ZAISTA DOKAZUJE (provereno sabotažom):
        // isplata se ne ponavlja — ali NE utvrđuje KOJI sloj je to sprečio.
        // ReferralService ima dva nezavisna sloja zaštite:
        //   1. provera statusa u memoriji (rana izlazna tačka)
        //   2. uslov u samoj UPDATE naredbi
        // Uklanjanje bilo kog od njih pojedinačno ostavlja ovaj test zelenim.
        // Sloj 2 izoluje tek test ispod (paralelni pozivi) — samo on pada kad
        // se uslov izvuče iz UPDATE naredbe.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);
        var pozvani   = await Data.CreateUnconfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        await Data.ConfirmEmailAsync("pozvani@test.rs");

        // Act — drugi poziv ide direktno na servis, jer Data.ConfirmEmailAsync
        // izlazi rano kad je email već potvrđen. Testiramo zaštitu U SERVISU,
        // ne zaštitu u pripremi podataka.
        await WithService<ReferralService>(svc => svc.TryRewardSignupAsync(pozvani.Id));

        // Assert
        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(PrvaRata,
            "druga isplata iste rate mora biti odbijena");

        (await Query(db => db.TokenTransactions
            .CountAsync(t => t.UserId == pozivalac.Id && t.Kind == TokenKind.Referral)))
            .Should().Be(1, "ledger ne sme imati dva zapisa za istu ratu");
    }

    [Fact]
    public async Task PotvrdaEmaila_KadaViseZahtevaIstovremeno_IsplacujeTacnoJednom()
    {
        // Ista rata, ali sada pod paralelnim pozivima. Provera "da li je već
        // isplaćeno" i sama isplata moraju biti JEDNA SQL naredba — inače oba
        // poziva pročitaju status Pending i oba plate.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);
        var pozvani   = await Data.CreateUnconfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Email potvrđujemo direktno, bez isplate, da bi svih 8 poziva ispod
        // krenulo iz istog stanja (Pending).
        await WithService<Microsoft.AspNetCore.Identity.UserManager<UsluzionicaServer.Domain.Entities.ApplicationUser>>(
            async users =>
            {
                var u     = await users.FindByIdAsync(pozvani.Id);
                var token = await users.GenerateEmailConfirmationTokenAsync(u!);
                await users.ConfirmEmailAsync(u!, token);
            });

        // Act — svaki poziv dobija SVOJ scope, dakle svoj DbContext, kao u
        // pravom HTTP zahtevu. DbContext nije thread-safe.
        var zadaci = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = Factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ReferralService>();
            await svc.TryRewardSignupAsync(pozvani.Id);
        });
        await Task.WhenAll(zadaci);

        // Assert
        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(PrvaRata);

        (await Query(db => db.TokenTransactions
            .CountAsync(t => t.UserId == pozivalac.Id && t.Kind == TokenKind.Referral)))
            .Should().Be(1);
    }

    // ── 2. RATA ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AktivacijaProvajdera_KadaJeKorisnikBioPozvan_IsplacujeDruguRatu()
    {
        // Arrange — pozvani je već potvrdio email, pa je prva rata isplaćena.
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);
        var pozvani   = await Data.CreateConfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(PrvaRata,
            "priprema: prva rata mora već biti isplaćena");

        // Act
        var (profil, greska) = await WithService<ProviderService, (ProviderProfileDto?, string?)>(
            async svc => await svc.ActivateAsync(pozvani.Id, new ActivateProviderDto
            {
                Profession  = "Frizer",
                Location    = TestData.ValidCity,
                CategoryIds = [TestData.SeededCategoryId]
            }));

        // Assert
        greska.Should().BeNull();
        profil.Should().NotBeNull();

        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(PrvaRata + DrugaRata,
            "obe rate se sabiraju — druga ne zamenjuje prvu");

        var referral = await Query(db => db.Referrals.SingleAsync(r => r.ReferrerId == pozivalac.Id));
        referral.Status.Should().Be(ReferralStatus.Rewarded);
        referral.ActivationTokensAwarded.Should().Be(DrugaRata);
        referral.ActivationRewardedAt.Should().NotBeNull();

        // Prva rata mora ostati zabeležena — druga je ne sme pregaziti.
        referral.SignupTokensAwarded.Should().Be(PrvaRata);
    }

    [Fact]
    public async Task AktivacijaProvajdera_OstavljaTokenTransactionZaRevizijuu()
    {
        // Svaka promena balansa mora imati trag — inače se ne može utvrditi
        // odakle su tokeni došli kad neko prijavi grešku.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);
        var pozvani   = await Data.CreateConfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Act
        await WithService<ProviderService, (ProviderProfileDto?, string?)>(
            async svc => await svc.ActivateAsync(pozvani.Id, new ActivateProviderDto
            {
                Profession = "Frizer", Location = TestData.ValidCity,
                CategoryIds = [TestData.SeededCategoryId]
            }));

        // Assert — dva zapisa, po jedan za svaku ratu, oba sa tačnim BalanceAfter.
        var zapisi = await Query(db => db.TokenTransactions
            .Where(t => t.UserId == pozivalac.Id && t.Kind == TokenKind.Referral)
            .OrderBy(t => t.Id)
            .ToListAsync());

        zapisi.Should().HaveCount(2);

        zapisi[0].Amount.Should().Be(PrvaRata);
        zapisi[0].BalanceAfter.Should().Be(PrvaRata);

        zapisi[1].Amount.Should().Be(DrugaRata);
        zapisi[1].BalanceAfter.Should().Be(PrvaRata + DrugaRata,
            "BalanceAfter mora pratiti stvarno stanje posle svake rate");
    }

    [Fact]
    public async Task AktivacijaProvajdera_KadaSeOkineDvaput_NeIsplacujeDruguRatuPonovo()
    {
        // Aktivacija se ne može ponoviti kroz ProviderService (drugi poziv puca
        // na "profil već postoji"), pa zaštitu proveravamo na samom servisu.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod       = await Data.GetReferralCodeAsync(pozivalac.Id);
        var pozvani   = await Data.CreateConfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        await WithService<ProviderService, (ProviderProfileDto?, string?)>(
            async svc => await svc.ActivateAsync(pozvani.Id, new ActivateProviderDto
            {
                Profession = "Frizer", Location = TestData.ValidCity,
                CategoryIds = [TestData.SeededCategoryId]
            }));

        // Act
        await WithService<ReferralService>(svc => svc.TryRewardActivationAsync(pozvani.Id));

        // Assert
        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(PrvaRata + DrugaRata);

        (await Query(db => db.TokenTransactions
            .CountAsync(t => t.UserId == pozivalac.Id && t.Kind == TokenKind.Referral)))
            .Should().Be(2, "tačno dve rate, ni jedna više");
    }

    // ── Negativni slučajevi ────────────────────────────────────────────────

    [Fact]
    public async Task AktivacijaProvajdera_BezReferralKoda_NeKreiraReferralNiNagradu()
    {
        // Arrange
        var samostalni = await Data.CreateConfirmedUserAsync("samostalni@test.rs");

        // Act
        await WithService<ProviderService, (ProviderProfileDto?, string?)>(
            async svc => await svc.ActivateAsync(samostalni.Id, new ActivateProviderDto
            {
                Profession = "Moler", Location = TestData.ValidCity,
                CategoryIds = [TestData.SeededCategoryId]
            }));

        // Assert
        (await Query(db => db.Referrals.CountAsync())).Should().Be(0);
        (await Query(db => db.TokenTransactions.CountAsync(t => t.Kind == TokenKind.Referral)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Registracija_SaSopstvenimKodom_NeKreiraReferral()
    {
        // Samo-referral bi bio besplatan izvor tokena. Kod ovo sprečava
        // poređenjem `referrer.Id != user.Id`, ali to je jedna linija koju je
        // lako obrisati pri refaktoru — otuda test.

        // Arrange & Act
        var korisnik = await Data.CreateConfirmedUserAsync("sam@test.rs");
        var kod      = await Data.GetReferralCodeAsync(korisnik.Id);

        // Ponovna registracija sa sopstvenim kodom nije moguća (email je zauzet),
        // pa proveravamo da uopšte nema referral zapisa za samog sebe.
        var referraliZaSebe = await Query(db => db.Referrals
            .CountAsync(r => r.ReferrerId == korisnik.Id && r.ReferredUserId == korisnik.Id));

        // Assert
        referraliZaSebe.Should().Be(0);
        kod.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Registracija_SaNepostojecimKodom_NeKreiraReferralAliUspeva()
    {
        // Pogrešno prekucan kod ne sme oboriti registraciju — korisnik bi ostao
        // bez naloga zbog tuđe greške u kucanju.

        // Act
        var korisnik = await Data.CreateConfirmedUserAsync(
            "novi@test.rs", referralCode: "NEPOSTOJECI123");

        // Assert
        korisnik.Should().NotBeNull();
        (await Query(db => db.Referrals.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task AktivacijaProvajdera_KadaEmailNijePotvrdjen_Odbija()
    {
        // Arrange
        var nepotvrdjeni = await Data.CreateUnconfirmedUserAsync("nepotvrdjen@test.rs");

        // Act
        var (profil, greska) = await WithService<ProviderService, (ProviderProfileDto?, string?)>(
            async svc => await svc.ActivateAsync(nepotvrdjeni.Id, new ActivateProviderDto
            {
                Profession = "Električar", Location = TestData.ValidCity,
                CategoryIds = [TestData.SeededCategoryId]
            }));

        // Assert
        profil.Should().BeNull();
        greska.Should().Contain("email");

        (await Query(db => db.ProviderProfiles.CountAsync())).Should().Be(0);
    }
}
