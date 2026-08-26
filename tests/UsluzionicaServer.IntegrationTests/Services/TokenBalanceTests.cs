using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Tokens;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravila oko trošenja tokena na boost.
///
/// Zašto baš ovde: boost je jedino mesto gde korisnik SAM smanjuje svoj balans,
/// pa je i jedino mesto gde greška vodi u minus. Svako pravilo ima i pozitivan
/// i negativan test — test srećnog slučaja dokazuje samo da kod ne puca, ne i
/// da pravilo postoji. Da je provera balansa obrisana, pola testova ispod bi
/// i dalje prolazilo; padaju tek negativni.
///
/// Za trke pod paralelnim zahtevima vidi <see cref="TokenConcurrencyTests"/>.
/// </summary>
public class TokenBalanceTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const decimal PocetniBalans = 20m;

    /// <summary>Provajder sa oglasom i balansom — polazno stanje za većinu testova.</summary>
    private async Task<(string UserId, int ListingId)> PripremiProvajderaAsync(
        string email = "provajder@test.rs", decimal balans = PocetniBalans)
    {
        var (user, _) = await Data.CreateProviderAsync(email);
        await Data.SetTokenBalanceAsync(user.Id, balans);
        var listingId = await Data.CreateActiveListingAsync(user.Id);
        return (user.Id, listingId);
    }

    private Task<(bool Uspeh, string? Greska)> BoostAsync(
        int listingId, string userId, decimal tokeni, int dana = 7)
        => WithService<BoostService, (bool, string?)>(
            svc => svc.BoostListingAsync(listingId, userId, new BoostListingDto
            {
                TokensToSpend = tokeni,
                DurationDays  = dana
            }));

    // ── 1. Boost skida tačan iznos ─────────────────────────────────────────

    [Fact]
    public async Task Boost_KadaImaDovoljnoTokena_SkidaTacnoTrazeniIznos()
    {
        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync();

        // Act
        var (uspeh, greska) = await BoostAsync(listingId, userId, tokeni: 6m);

        // Assert
        uspeh.Should().BeTrue(greska);

        // "Tačan iznos" znači i da nije skinuto previše I da nije premalo.
        // Be(14m) hvata oba; BeLessThan(20m) bi propustio grešku od 100 tokena.
        (await Data.GetTokenBalanceAsync(userId)).Should().Be(PocetniBalans - 6m);
    }

    // ── 2. Nedovoljno tokena → odbijen, balans nepromenjen ─────────────────

    [Fact]
    public async Task Boost_KadaNemaDovoljnoTokena_OdbijaIOstavljaBalansNetaknut()
    {
        // NEGATIVAN TEST za pravilo iz testa 1. Bez njega bi kod koji uopšte
        // ne proverava balans prošao — samo bi odveo korisnika u minus.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync(balans: 5m);

        // Act
        var (uspeh, greska) = await BoostAsync(listingId, userId, tokeni: 10m);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("Nedovoljno tokena");

        (await Data.GetTokenBalanceAsync(userId)).Should().Be(5m,
            "neuspeo boost ne sme dodirnuti balans");

        // Neuspeh ne sme ostaviti ni ledger zapis ni boost.
        (await Query(db => db.TokenTransactions.CountAsync(t => t.UserId == userId)))
            .Should().Be(0);
        (await Query(db => db.ListingBoosts.CountAsync())).Should().Be(0);
        (await Query(db => db.Listings.Where(l => l.Id == listingId)
            .Select(l => l.IsBoosted).FirstAsync())).Should().BeFalse();
    }

    // ── 3. Trošenje do nule je dozvoljeno ──────────────────────────────────

    [Fact]
    public async Task Boost_KadaTrosiTacnoCeoBalans_ProlaziIOstavljaNulu()
    {
        // Granični slučaj koji lako pukne: provera mora biti `balans >= iznos`,
        // a ne `balans > iznos`. Razlika se vidi SAMO na tačno jednakim
        // vrednostima — otuda zaseban test.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync(balans: 7m);

        // Act
        var (uspeh, greska) = await BoostAsync(listingId, userId, tokeni: 7m);

        // Assert
        uspeh.Should().BeTrue(greska);
        (await Data.GetTokenBalanceAsync(userId)).Should().Be(0m);
    }

    [Fact]
    public async Task Boost_KadaFaliJedanCentimTokena_Odbija()
    {
        // Druga strana iste granice: `>=` sme propustiti jednakost, ali ne i
        // manjak. Par sa testom iznad zaključava tačnu granicu.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync(balans: 6.99m);

        // Act
        var (uspeh, _) = await BoostAsync(listingId, userId, tokeni: 7m);

        // Assert
        uspeh.Should().BeFalse();
        (await Data.GetTokenBalanceAsync(userId)).Should().Be(6.99m);
    }

    // ── 4. Nevalidno trajanje se odbija ────────────────────────────────────

    [Theory]
    [InlineData(1)]    // ispod najmanjeg dozvoljenog
    [InlineData(5)]    // između dozvoljenih vrednosti
    [InlineData(30)]   // iznad najvećeg
    [InlineData(0)]
    [InlineData(-7)]   // negativno bi dalo boost koji istekao u prošlosti
    public async Task Boost_KadaTrajanjeNijeDozvoljeno_OdbijaPreNegoSkineTokene(int dana)
    {
        // Validacija trajanja MORA ići pre skidanja tokena. Da ide posle,
        // korisnik bi platio boost koji nikad nije napravljen.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync();

        // Act
        var (uspeh, greska) = await BoostAsync(listingId, userId, tokeni: 6m, dana: dana);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("3, 7 ili 14");
        (await Data.GetTokenBalanceAsync(userId)).Should().Be(PocetniBalans);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(14)]
    public async Task Boost_KadaJeTrajanjeDozvoljeno_Prolazi(int dana)
    {
        // POZITIVAN par prethodnom testu. Bez ovoga bi i kod koji odbija SVE
        // vrednosti prošao Theory iznad.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync();

        // Act
        var (uspeh, greska) = await BoostAsync(listingId, userId, tokeni: 6m, dana: dana);

        // Assert
        uspeh.Should().BeTrue(greska);
    }

    [Fact]
    public async Task Boost_KadaJeIznosNulaIliNegativan_Odbija()
    {
        // Negativan iznos bi kroz `balans - iznos` POVEĆAO balans — besplatan
        // izvor tokena.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync();

        // Act
        var (uspehNula, _)      = await BoostAsync(listingId, userId, tokeni: 0m);
        var (uspehNegativan, _) = await BoostAsync(listingId, userId, tokeni: -5m);

        // Assert
        uspehNula.Should().BeFalse();
        uspehNegativan.Should().BeFalse();
        (await Data.GetTokenBalanceAsync(userId)).Should().Be(PocetniBalans,
            "negativan iznos ne sme uvećati balans");
    }

    // ── 5. Tuđi oglas se ne može boost-ovati ───────────────────────────────

    [Fact]
    public async Task Boost_KadaOglasNijeNjegov_OdbijaINeSkidaTokene()
    {
        // Bez ove provere bi korisnik mogao da troši SVOJE tokene na TUĐI oglas.
        // Zvuči bezopasno, ali znači da konkurent može da ti pomeri oglase u
        // rangiranju — ili, gore, da ti neko plati boost pa traži uslugu.

        // Arrange
        var (vlasnik, _)   = await Data.CreateProviderAsync("vlasnik@test.rs");
        var tudjiListingId = await Data.CreateActiveListingAsync(vlasnik.Id);

        var (napadac, _) = await Data.CreateProviderAsync("napadac@test.rs");
        await Data.SetTokenBalanceAsync(napadac.Id, PocetniBalans);

        // Act
        var (uspeh, greska) = await BoostAsync(tudjiListingId, napadac.Id, tokeni: 6m);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("sopstvene");

        (await Data.GetTokenBalanceAsync(napadac.Id)).Should().Be(PocetniBalans);
        (await Query(db => db.Listings.Where(l => l.Id == tudjiListingId)
            .Select(l => l.IsBoosted).FirstAsync())).Should().BeFalse();
    }

    [Fact]
    public async Task Boost_KadaOglasNePostoji_Odbija()
    {
        // Arrange
        var (userId, _) = await PripremiProvajderaAsync();

        // Act
        var (uspeh, greska) = await BoostAsync(listingId: 999_999, userId: userId, tokeni: 6m);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije pronađen");
        (await Data.GetTokenBalanceAsync(userId)).Should().Be(PocetniBalans);
    }

    // ── 6. Svaka promena ostavlja TokenTransaction ─────────────────────────

    [Fact]
    public async Task Boost_SvakaUspesnaPromenaBalansa_OstavljaTacanLedgerZapis()
    {
        // Ledger je jedini način da se posle utvrdi odakle je balans došao.
        // Ako se razmimoiđe sa stvarnim balansom, svaka reklamacija postaje
        // nerešiva.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync();

        // Act — dva boosta, da se proveri i da se BalanceAfter pomera
        await BoostAsync(listingId, userId, tokeni: 6m);
        await BoostAsync(listingId, userId, tokeni: 4m);

        // Assert
        var zapisi = await Query(db => db.TokenTransactions
            .Where(t => t.UserId == userId && t.Kind == TokenKind.BoostSpend)
            .OrderBy(t => t.Id)
            .ToListAsync());

        zapisi.Should().HaveCount(2);

        // Trošenje je NEGATIVAN iznos — znak nosi informaciju o smeru.
        zapisi[0].Amount.Should().Be(-6m);
        zapisi[1].Amount.Should().Be(-4m);

        zapisi[0].BalanceAfter.Should().Be(14m);
        zapisi[1].BalanceAfter.Should().Be(10m);

        // ReferenceId povezuje zapis sa oglasom — bez toga se ne zna na šta je
        // potrošeno.
        zapisi.Should().OnlyContain(t => t.ReferenceId == listingId);

        // Najvažnija provera: ledger i stvarni balans moraju se poklapati.
        var stvarni = await Data.GetTokenBalanceAsync(userId);
        (PocetniBalans + zapisi.Sum(t => t.Amount)).Should().Be(stvarni);
    }

    [Fact]
    public async Task Boost_UspesanBoost_PostavljaBoostScoreIRokTrajanja()
    {
        // Prateći efekti boosta. BoostScore = tokeni / dani, i ADITIVAN je —
        // dva boosta se sabiraju, ne zamenjuju.

        // Arrange
        var (userId, listingId) = await PripremiProvajderaAsync();

        // Act
        await BoostAsync(listingId, userId, tokeni: 6m, dana: 3);   // → +2.0
        await BoostAsync(listingId, userId, tokeni: 7m, dana: 7);   // → +1.0

        // Assert
        var listing = await Query(db => db.Listings.SingleAsync(l => l.Id == listingId));

        listing.IsBoosted.Should().BeTrue();
        listing.BoostScore.Should().Be(3.0m, "BoostScore se sabira preko boostova");
        listing.BoostExpiresAt.Should().NotBeNull();

        // Rok je NAJKASNIJI od svih aktivnih boostova (7 dana > 3 dana).
        listing.BoostExpiresAt!.Value.Should().BeAfter(DateTime.UtcNow.AddDays(6));

        (await Query(db => db.ListingBoosts.CountAsync())).Should().Be(2);
    }
}
