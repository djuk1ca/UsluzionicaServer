using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Tokens;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo koje se ne vidi ni u jednom pojedinačnom zahtevu:
/// **token balans ne sme otići u minus ni pod istovremenim zahtevima.**
///
/// Zašto ovo nije pokriveno običnim testom: `BoostService` proverava balans
/// (`if (user.TokenBalance < dto.TokensToSpend)`) pa ga umanjuje. Između te
/// dve radnje postoji prozor. Sekvencijalno je sve ispravno; paralelno oba
/// zahteva pročitaju isti balans i oba prođu proveru.
///
/// Ovo je klasičan read-modify-write problem. Novac je u pitanju, pa je
/// vredan testa čak i ako se u praksi retko dešava.
/// </summary>
public class TokenConcurrencyTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const int ParalelnihZahteva = 8;
    private const decimal CenaBoosta    = 10m;

    [Fact]
    public async Task Boost_KadaViseZahtevaIstovremenoTrosiIstiBalans_BalansNikadNeIdeUMinus()
    {
        // ── Arrange ────────────────────────────────────────────────────────
        // Balans tačno za JEDAN boost, a šaljemo osam zahteva odjednom.
        var (provider, _) = await Data.CreateProviderAsync("provajder@test.rs");
        await Data.SetTokenBalanceAsync(provider.Id, CenaBoosta);

        var listingId = await Data.CreateActiveListingAsync(provider.Id);

        // ── Act ────────────────────────────────────────────────────────────
        // Svaki zahtev dobija SVOJ scope, dakle svoj DbContext — kao što bi
        // dobio i u pravom HTTP zahtevu. DbContext nije thread-safe, pa bi
        // deljenje jedne instance testiralo pogrešnu stvar.
        var zadaci = Enumerable.Range(0, ParalelnihZahteva).Select(async _ =>
        {
            using var scope = Factory.Services.CreateScope();
            var boost = scope.ServiceProvider.GetRequiredService<BoostService>();

            var (uspeh, _) = await boost.BoostListingAsync(listingId, provider.Id, new BoostListingDto
            {
                TokensToSpend = CenaBoosta,
                DurationDays  = 7
            });
            return uspeh;
        });

        var rezultati = await Task.WhenAll(zadaci);

        // ── Assert ─────────────────────────────────────────────────────────
        var konacniBalans = await Data.GetTokenBalanceAsync(provider.Id);

        konacniBalans.Should().BeGreaterThanOrEqualTo(0m,
            "korisnik ne sme potrošiti više tokena nego što ima");

        var uspesnih = rezultati.Count(r => r);
        uspesnih.Should().Be(1,
            "balans pokriva tačno jedan boost, pa tačno jedan zahtev sme proći");

        konacniBalans.Should().Be(0m);
    }

    [Fact]
    public async Task Boost_KadaViseZahtevaIstovremeno_BrojTransakcijaOdgovaraSkinutomIznosu()
    {
        // Druga strana istog problema: čak i da balans nekim čudom ostane
        // nenegativan, ledger mora biti konzistentan — zbir svih BoostSpend
        // transakcija mora tačno odgovarati stvarno skinutom iznosu.

        // Arrange
        var (provider, _) = await Data.CreateProviderAsync("provajder2@test.rs");
        await Data.SetTokenBalanceAsync(provider.Id, CenaBoosta);
        var listingId = await Data.CreateActiveListingAsync(provider.Id);

        // Act
        var zadaci = Enumerable.Range(0, ParalelnihZahteva).Select(async _ =>
        {
            using var scope = Factory.Services.CreateScope();
            var boost = scope.ServiceProvider.GetRequiredService<BoostService>();
            await boost.BoostListingAsync(listingId, provider.Id, new BoostListingDto
            {
                TokensToSpend = CenaBoosta, DurationDays = 7
            });
        });
        await Task.WhenAll(zadaci);

        // Assert
        var potroseno = await Query(db => db.TokenTransactions
            .Where(t => t.UserId == provider.Id && t.Kind == TokenKind.BoostSpend)
            .SumAsync(t => t.Amount));

        var konacniBalans = await Data.GetTokenBalanceAsync(provider.Id);

        // Amount je negativan za trošenje, otuda +.
        (CenaBoosta + potroseno).Should().Be(konacniBalans,
            "početni balans minus zbir transakcija mora dati konačni balans");
    }
}
