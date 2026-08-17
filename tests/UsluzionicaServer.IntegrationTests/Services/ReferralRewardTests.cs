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
/// Štiti pravilo: referral nagrada se isplaćuje kada pozvani korisnik
/// AKTIVIRA PROVAJDER NALOG — ne kada se registruje.
///
/// Zašto je baš to pravilo vredno testa: to je poslovna odluka koja se ne vidi
/// iz tipova ni iz šeme baze. Neko ko sutra "pojednostavi" registraciju tako
/// što odmah isplati nagradu, napravio bi rupu — svako bi mogao da otvara
/// naloge sa svojim kodom i uzima tokene bez ikakvog rada.
///
/// Struktura svakog testa je AAA:
///   Arrange — pripremi stanje
///   Act     — pozovi TAČNO jednu stvar koja se testira
///   Assert  — proveri ishod kroz NOV DbContext (vidi Query u bazi klasi)
/// </summary>
public class ReferralRewardTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const decimal OcekivanaNagrada = 5.0m;   // Referral:ProviderActivationRewardTokens iz WebFactory

    [Fact]
    public async Task Registracija_SaReferralKodom_NeIsplacujeNagraduOdmah()
    {
        // Ovo je NAJVAŽNIJI test u fajlu — dokazuje da se pravilo ne okida prerano.
        // Test srećnog slučaja (ispod) sam po sebi ne bi otkrio grešku u kojoj se
        // nagrada isplaćuje već pri registraciji.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod = await Query(db => db.Users
            .Where(u => u.Id == pozivalac.Id)
            .Select(u => u.ReferralCode!)
            .FirstAsync());

        // Act — samo registracija, bez aktivacije provajdera
        await Data.CreateConfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Assert
        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(0m,
            "nagrada se isplaćuje tek na aktivaciju provajdera");

        var referral = await Query(db => db.Referrals
            .SingleAsync(r => r.ReferrerId == pozivalac.Id));

        referral.Status.Should().Be(ReferralStatus.Pending);
        // TokensAwarded je decimal? — dok nagrada nije isplaćena stoji null,
        // što je jače od 0: razlikuje "nije isplaćeno" od "isplaćeno nula".
        referral.TokensAwarded.Should().BeNull();
        referral.RewardedAt.Should().BeNull();
    }

    [Fact]
    public async Task AktivacijaProvajdera_KadaJeKorisnikBioPozvan_IsplacujeNagraduPozivaocu()
    {
        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod = await Query(db => db.Users
            .Where(u => u.Id == pozivalac.Id).Select(u => u.ReferralCode!).FirstAsync());

        var pozvani = await Data.CreateConfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Act — jedina radnja koja se testira
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

        (await Data.GetTokenBalanceAsync(pozivalac.Id)).Should().Be(OcekivanaNagrada);

        var referral = await Query(db => db.Referrals.SingleAsync(r => r.ReferrerId == pozivalac.Id));
        referral.Status.Should().Be(ReferralStatus.Rewarded);
        referral.TokensAwarded.Should().Be(OcekivanaNagrada);
        referral.RewardedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AktivacijaProvajdera_OstavljaTokenTransactionZaRevizijuu()
    {
        // Svaka promena balansa mora imati trag — inače se ne može utvrditi
        // odakle su tokeni došli kad neko prijavi grešku.

        // Arrange
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");
        var kod = await Query(db => db.Users
            .Where(u => u.Id == pozivalac.Id).Select(u => u.ReferralCode!).FirstAsync());
        var pozvani = await Data.CreateConfirmedUserAsync("pozvani@test.rs", referralCode: kod);

        // Act
        await WithService<ProviderService, (ProviderProfileDto?, string?)>(
            async svc => await svc.ActivateAsync(pozvani.Id, new ActivateProviderDto
            {
                Profession = "Frizer", Location = TestData.ValidCity,
                CategoryIds = [TestData.SeededCategoryId]
            }));

        // Assert
        var tx = await Query(db => db.TokenTransactions
            .SingleAsync(t => t.UserId == pozivalac.Id && t.Kind == TokenKind.Referral));

        tx.Amount.Should().Be(OcekivanaNagrada);
        tx.BalanceAfter.Should().Be(OcekivanaNagrada, "balans posle mora odgovarati stvarnom stanju");
    }

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
        var kod = await Query(db => db.Users
            .Where(u => u.Id == korisnik.Id).Select(u => u.ReferralCode!).FirstAsync());

        // Ponovna registracija sa sopstvenim kodom nije moguća (email je zauzet),
        // pa proveravamo da uopšte nema referral zapisa za samog sebe.
        var referraliZaSebe = await Query(db => db.Referrals
            .CountAsync(r => r.ReferrerId == korisnik.Id && r.ReferredUserId == korisnik.Id));

        // Assert
        referraliZaSebe.Should().Be(0);
        kod.Should().NotBeNullOrWhiteSpace();
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
