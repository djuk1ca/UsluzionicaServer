using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.DiscountOffers;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravila oko token ponuda (popust koji klijent nudi provajderu).
///
/// Ključna razlika u odnosu na boost: ovde se tokeni PRENOSE između dva
/// korisnika. Prenos ima dve strane, pa svaka greška ili stvara tokene ni iz
/// čega ili ih uništava. Zato skoro svaki test proverava OBA balansa, ne samo
/// onaj koji se očekivano menja.
///
/// Tokeni se NE rezervišu pri slanju ponude — proveravaju se tek pri
/// prihvatanju. To je svesna odluka (ponuda ne sme da zamrzne novac koji
/// korisnik u međuvremenu želi da potroši), ali znači da je razmak između
/// slanja i prihvatanja mesto gde balans može da nestane.
/// </summary>
public class DiscountOfferTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const decimal PocetniBalans = 20m;
    private const decimal IznosPonude   = 5m;

    /// <summary>Klijent sa balansom, provajder sa oglasom, i poslata ponuda.</summary>
    private async Task<(string KlijentId, string ProvajderId, int OfferId)>
        PripremiPonuduAsync(decimal balansKlijenta = PocetniBalans)
    {
        var (provajder, _) = await Data.CreateProviderAsync("provajder@test.rs");
        var listingId      = await Data.CreateActiveListingAsync(provajder.Id);

        var klijent = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        await Data.SetTokenBalanceAsync(klijent.Id, balansKlijenta);

        var (ponuda, greska) = await WithService<TokenWalletService, (DiscountOfferDto?, string?)>(
            svc => svc.CreateOfferAsync(klijent.Id, new CreateDiscountOfferDto
            {
                ReceiverId  = provajder.Id,
                ListingId   = listingId,
                TokenAmount = IznosPonude
            }));

        if (ponuda is null)
            throw new InvalidOperationException($"Priprema ponude nije uspela: {greska}");

        return (klijent.Id, provajder.Id, ponuda.Id);
    }

    private Task<(bool Uspeh, string? Greska)> PrihvatiAsync(int offerId, string receiverId)
        => WithService<TokenWalletService, (bool, string?)>(
            svc => svc.AcceptOfferAsync(offerId, receiverId));

    private Task<(bool Uspeh, string? Greska)> OdbijAsync(int offerId, string receiverId)
        => WithService<TokenWalletService, (bool, string?)>(
            svc => svc.RejectOfferAsync(offerId, receiverId));

    // ── 1. Prihvatanje prebacuje tokene ────────────────────────────────────

    [Fact]
    public async Task Prihvatanje_KadaJeSveIspravno_PrebacujeTokeneSaPosiljaocaNaPrimaoca()
    {
        // Arrange
        var (klijentId, provajderId, offerId) = await PripremiPonuduAsync();

        // Act
        var (uspeh, greska) = await PrihvatiAsync(offerId, provajderId);

        // Assert
        uspeh.Should().BeTrue(greska);

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(PocetniBalans - IznosPonude);
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(IznosPonude);

        // Prenos ne sme ni stvoriti ni uništiti tokene — zbir mora ostati isti.
        // Ovo hvata greške koje pojedinačne provere propuštaju: npr. da je
        // primaocu dodat pogrešan iznos.
        var zbir = await Data.GetTokenBalanceAsync(klijentId)
                 + await Data.GetTokenBalanceAsync(provajderId);
        zbir.Should().Be(PocetniBalans);

        var ponuda = await Query(db => db.DiscountTokenOffers.SingleAsync(o => o.Id == offerId));
        ponuda.Status.Should().Be(DiscountOfferStatus.Accepted);
        ponuda.RespondedAt.Should().NotBeNull();

        // Ledger mora imati OBE strane prenosa.
        var zapisi = await Query(db => db.TokenTransactions
            .Where(t => t.ReferenceId == offerId)
            .ToListAsync());

        zapisi.Should().HaveCount(2);
        zapisi.Single(t => t.Kind == TokenKind.DiscountSent).Amount.Should().Be(-IznosPonude);
        zapisi.Single(t => t.Kind == TokenKind.DiscountReceived).Amount.Should().Be(IznosPonude);
    }

    // ── 2. Prihvatanje već prihvaćene puca ─────────────────────────────────

    [Fact]
    public async Task Prihvatanje_KadaJePonudaVecPrihvacena_OdbijaINePrebacujePonovo()
    {
        // Bez ove provere bi dvostruki klik na "Prihvati" prebacio tokene dvaput.
        // Dupla isplata iz jedne ponude je najskuplja moguća greška ovde.

        // Arrange
        var (klijentId, provajderId, offerId) = await PripremiPonuduAsync();
        await PrihvatiAsync(offerId, provajderId);

        // Act
        var (uspeh, greska) = await PrihvatiAsync(offerId, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije aktivna");

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(PocetniBalans - IznosPonude,
            "drugo prihvatanje ne sme ponovo skinuti tokene");
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(IznosPonude);

        (await Query(db => db.TokenTransactions.CountAsync(t => t.ReferenceId == offerId)))
            .Should().Be(2, "tačno jedan prenos = tačno dva ledger zapisa");
    }

    // ── 3. Odbijanje već odbijene puca ─────────────────────────────────────

    [Fact]
    public async Task Odbijanje_KadaJePonudaVecOdbijena_Odbija()
    {
        // Arrange
        var (_, provajderId, offerId) = await PripremiPonuduAsync();
        await OdbijAsync(offerId, provajderId);

        // Act
        var (uspeh, greska) = await OdbijAsync(offerId, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije aktivna");
    }

    [Fact]
    public async Task Prihvatanje_KadaJePonudaVecOdbijena_Odbija()
    {
        // Odbijena ponuda ne sme da "vaskrsne" kroz prihvatanje. Status je
        // jedan, pa je i prelaz jedan — iz Pending, i nigde drugde.

        // Arrange
        var (klijentId, provajderId, offerId) = await PripremiPonuduAsync();
        await OdbijAsync(offerId, provajderId);

        // Act
        var (uspeh, _) = await PrihvatiAsync(offerId, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(PocetniBalans);

        var ponuda = await Query(db => db.DiscountTokenOffers.SingleAsync(o => o.Id == offerId));
        ponuda.Status.Should().Be(DiscountOfferStatus.Rejected,
            "neuspeo prelaz ne sme promeniti status");
    }

    // ── 4. Tuđa ponuda puca ────────────────────────────────────────────────

    [Fact]
    public async Task Prihvatanje_KadaKorisnikNijePrimalac_OdbijaINePrebacujeNista()
    {
        // Da ovo prođe, bilo ko bi mogao da prihvati tuđu ponudu i uzme tokene
        // koji su namenjeni drugome. Provera ide kroz `o.ReceiverId == receiverId`
        // u samom upitu — ponuda se za pogrešnog korisnika i ne pronađe.

        // Arrange
        var (klijentId, _, offerId) = await PripremiPonuduAsync();
        var uljez = await Data.CreateConfirmedUserAsync("uljez@test.rs");

        // Act
        var (uspeh, greska) = await PrihvatiAsync(offerId, uljez.Id);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije pronađena");

        (await Data.GetTokenBalanceAsync(uljez.Id)).Should().Be(0m);
        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(PocetniBalans);

        var ponuda = await Query(db => db.DiscountTokenOffers.SingleAsync(o => o.Id == offerId));
        ponuda.Status.Should().Be(DiscountOfferStatus.Pending);
    }

    [Fact]
    public async Task Odbijanje_KadaKorisnikNijePrimalac_Odbija()
    {
        // Ista provera na drugoj operaciji. Uljez ne sme ni da odbije tuđu
        // ponudu — to bi bio način da se sabotira konkurentski dogovor.

        // Arrange
        var (_, _, offerId) = await PripremiPonuduAsync();
        var uljez = await Data.CreateConfirmedUserAsync("uljez@test.rs");

        // Act
        var (uspeh, greska) = await OdbijAsync(offerId, uljez.Id);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nije pronađena");

        (await Query(db => db.DiscountTokenOffers.SingleAsync(o => o.Id == offerId)))
            .Status.Should().Be(DiscountOfferStatus.Pending);
    }

    // ── 5. Odbijanje ne pomera tokene ──────────────────────────────────────

    [Fact]
    public async Task Odbijanje_KadaJePonudaAktivna_MenjaStatusAliNePomeraTokene()
    {
        // Tokeni se pri slanju ne rezervišu, pa odbijanje nema šta da vrati.
        // Test je ovde da niko sutra ne "popravi" odbijanje tako što doda
        // povraćaj — jer bi to stvorilo tokene ni iz čega.

        // Arrange
        var (klijentId, provajderId, offerId) = await PripremiPonuduAsync();

        // Act
        var (uspeh, greska) = await OdbijAsync(offerId, provajderId);

        // Assert
        uspeh.Should().BeTrue(greska);

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(PocetniBalans);
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(0m);

        (await Query(db => db.TokenTransactions.CountAsync(t => t.ReferenceId == offerId)))
            .Should().Be(0, "odbijanje ne sme ostaviti nijedan ledger zapis");

        var ponuda = await Query(db => db.DiscountTokenOffers.SingleAsync(o => o.Id == offerId));
        ponuda.Status.Should().Be(DiscountOfferStatus.Rejected);
        ponuda.RespondedAt.Should().NotBeNull();
    }

    // ── 6. Pošiljalac bez tokena → puca ────────────────────────────────────

    [Fact]
    public async Task Prihvatanje_KadaPosiljalacUMedjuvremenuPotrosiTokene_OdbijaPrenos()
    {
        // NAJVAŽNIJI test u fajlu. Balans se proverava pri SLANJU ponude, ali
        // se tokeni ne rezervišu — pa klijent može poslati ponudu na 5 tokena,
        // pa ih sve potrošiti na boost, i tek onda provajder prihvata.
        //
        // Da provera pri prihvatanju ne postoji, prenos bi prošao i klijentov
        // balans bi otišao u minus, a provajder bi dobio tokene koji ne postoje.

        // Arrange
        var (klijentId, provajderId, offerId) = await PripremiPonuduAsync();

        // Klijent u međuvremenu ostaje bez tokena.
        await Data.SetTokenBalanceAsync(klijentId, 0m);

        // Act
        var (uspeh, greska) = await PrihvatiAsync(offerId, provajderId);

        // Assert
        uspeh.Should().BeFalse();
        greska.Should().Contain("nema dovoljno tokena");

        (await Data.GetTokenBalanceAsync(klijentId)).Should().Be(0m,
            "balans ne sme otići u minus");
        (await Data.GetTokenBalanceAsync(provajderId)).Should().Be(0m,
            "primalac ne sme dobiti tokene koji nisu skinuti");

        (await Query(db => db.TokenTransactions.CountAsync(t => t.ReferenceId == offerId)))
            .Should().Be(0);

        // Neuspeo prenos mora ostaviti ponudu aktivnom — klijent može dopuniti
        // balans pa provajder ponovo prihvatiti.
        (await Query(db => db.DiscountTokenOffers.SingleAsync(o => o.Id == offerId)))
            .Status.Should().Be(DiscountOfferStatus.Pending);
    }

    [Fact]
    public async Task Slanje_KadaPosiljalacNemaDovoljnoTokena_OdbijaOdmah()
    {
        // Rana provera pri slanju. Ne štiti od trke (vidi test iznad), ali daje
        // korisniku jasnu poruku umesto ponude koja će kasnije tiho pasti.

        // Arrange
        var (provajder, _) = await Data.CreateProviderAsync("provajder@test.rs");
        var listingId      = await Data.CreateActiveListingAsync(provajder.Id);

        var klijent = await Data.CreateConfirmedUserAsync("siromasan@test.rs");
        await Data.SetTokenBalanceAsync(klijent.Id, 1m);

        // Act
        var (ponuda, greska) = await WithService<TokenWalletService, (DiscountOfferDto?, string?)>(
            svc => svc.CreateOfferAsync(klijent.Id, new CreateDiscountOfferDto
            {
                ReceiverId  = provajder.Id,
                ListingId   = listingId,
                TokenAmount = 10m
            }));

        // Assert
        ponuda.Should().BeNull();
        greska.Should().Contain("Nedovoljno tokena");
        (await Query(db => db.DiscountTokenOffers.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Slanje_SamomeSebi_Odbija()
    {
        // Ponuda samom sebi bi bila prenos iz levog u desni džep — bezopasno po
        // balans, ali bi zaprljala ledger i statistiku.

        // Arrange
        var (provajder, _) = await Data.CreateProviderAsync("provajder@test.rs");
        var listingId      = await Data.CreateActiveListingAsync(provajder.Id);
        await Data.SetTokenBalanceAsync(provajder.Id, PocetniBalans);

        // Act
        var (ponuda, greska) = await WithService<TokenWalletService, (DiscountOfferDto?, string?)>(
            svc => svc.CreateOfferAsync(provajder.Id, new CreateDiscountOfferDto
            {
                ReceiverId  = provajder.Id,
                ListingId   = listingId,
                TokenAmount = IznosPonude
            }));

        // Assert
        ponuda.Should().BeNull();
        greska.Should().Contain("samome sebi");
    }
}
