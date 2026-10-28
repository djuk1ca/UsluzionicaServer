using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti promenu redosleda slika oglasa (PUT /api/listings/{id}/images/order).
///
/// Zašto je vredno testa: prva slika je glavna — ona je na kartici u pretrazi.
/// Ako redosled ne stigne do pretrage, korisnik vidi da je promenio glavnu
/// sliku, a svi ostali i dalje vide staru. Zato se ovde ne proverava samo
/// SortOrder u bazi, nego i šta vraćaju GetById i pretraga.
///
/// Drugi deo je ugovor o listi: tačno sve slike oglasa, svaka jednom. Svaki
/// način da se to prekrši (nepotpuna lista, strana slika, duplikat) mora biti
/// odbijen i ostaviti stari redosled netaknut.
///
/// Stanje moderacije po slici ne postoji (ListingImage ga nema), pa redosled
/// uvek obuhvata sve slike oglasa koje su u bazi.
/// </summary>
public class ListingImageOrderTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>Najmanji validan JPEG — isti kao u ImageModerationTests.</summary>
    private static IFormFile Slika()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46,
                       0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                       0xFF, 0xD9];

        var ms = new MemoryStream(jpeg);
        return new FormFile(ms, 0, ms.Length, "file", "test.jpg")
        {
            Headers     = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    /// <summary>Oglas sa <paramref name="brojSlika"/> slika, dodatih pravim upload-om.</summary>
    private async Task<(string MajstorId, int OglasId, List<int> SlikeIds)> PostaviAsync(
        int brojSlika = 3, string email = "majstor@test.rs")
    {
        var (majstor, _) = await Data.CreateProviderAsync(email);
        var oglasId = await Data.CreateActiveListingAsync(majstor.Id);

        var ids = new List<int>();
        for (var i = 0; i < brojSlika; i++)
        {
            var (slika, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
                svc => svc.UploadImageAsync(oglasId, majstor.Id, Slika()));

            slika.Should().NotBeNull(greska);
            ids.Add(slika!.Id);
        }

        return (majstor.Id, oglasId, ids);
    }

    private Task<(List<ListingImageDto>? Result, string? Error, bool NijePronadjeno)> PromeniAsync(
        int oglasId, string userId, IReadOnlyList<int> ids) =>
        WithService<ListingService, (List<ListingImageDto>?, string?, bool)>(
            svc => svc.ReorderImagesAsync(oglasId, userId, ids));

    /// <summary>Id-jevi slika oglasa u redosledu iz baze (kroz nov DbContext).</summary>
    private Task<List<int>> RedosledIzBazeAsync(int oglasId) =>
        Query(db => db.ListingImages
            .Where(i => i.ListingId == oglasId)
            .OrderBy(i => i.SortOrder)
            .Select(i => i.Id)
            .ToListAsync());

    // ── Uspeh ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoviRedosled_UpisujeSortOrderOdNule()
    {
        var (majstorId, oglasId, ids) = await PostaviAsync();
        var novi = new List<int> { ids[2], ids[0], ids[1] };

        var (rezultat, greska, nijePronadjeno) = await PromeniAsync(oglasId, majstorId, novi);

        greska.Should().BeNull();
        nijePronadjeno.Should().BeFalse();
        rezultat!.Select(s => s.Id).Should().Equal(novi, "odgovor vraća slike novim redom");
        rezultat!.Select(s => s.SortOrder).Should().Equal(0, 1, 2);

        var izBaze = await Query(db => db.ListingImages
            .Where(i => i.ListingId == oglasId)
            .ToDictionaryAsync(i => i.Id, i => i.SortOrder));

        izBaze[ids[2]].Should().Be(0);
        izBaze[ids[0]].Should().Be(1);
        izBaze[ids[1]].Should().Be(2);
    }

    [Fact]
    public async Task NoviRedosled_PrvaSlikaPostajeGlavnaUGetByIdIPretrazi()
    {
        // SRŽ FUNKCIJE. Glavna slika je prva po SortOrder-u — i u detaljima i
        // na kartici u pretrazi. Ako bilo koji od ta dva puta ne vidi novi red,
        // promena je samo kozmetika u bazi.
        var (majstorId, oglasId, ids) = await PostaviAsync();
        var novi = new List<int> { ids[1], ids[2], ids[0] };

        var (_, greska, _) = await PromeniAsync(oglasId, majstorId, novi);
        greska.Should().BeNull();

        var detalji = await WithService<ListingService, ListingDto?>(
            svc => svc.GetByIdAsync(oglasId));
        detalji!.Images.Select(s => s.Id).Should().Equal(novi);

        var pretraga = await WithService<ListingService, PagedResult<ListingDto>>(
            svc => svc.SearchAsync(new ListingQueryParams { Page = 1, PageSize = 50 }));
        var kartica = pretraga.Items.Single(l => l.Id == oglasId);

        kartica.Images.First().Id.Should().Be(ids[1], "kartica u pretrazi prikazuje novu glavnu sliku");
        kartica.Images.Select(s => s.Id).Should().Equal(novi);
    }

    [Fact]
    public async Task NoviRedosled_OsvezavaUpdatedAt()
    {
        var (majstorId, oglasId, ids) = await PostaviAsync();
        var pre = await Query(db => db.Listings
            .Where(l => l.Id == oglasId).Select(l => l.UpdatedAt).SingleAsync());

        await PromeniAsync(oglasId, majstorId, [ids[2], ids[1], ids[0]]);

        var posle = await Query(db => db.Listings
            .Where(l => l.Id == oglasId).Select(l => l.UpdatedAt).SingleAsync());
        posle.Should().BeAfter(pre);
    }

    [Fact]
    public async Task PosleBrisanjaSlike_ZatvaraRupuUSortOrder()
    {
        // DeleteImageAsync ostavlja rupu (0, 2). Redosled je prepisuje sa 0..n-1.
        var (majstorId, oglasId, ids) = await PostaviAsync();

        var (obrisana, _) = await WithService<ListingService, (bool, string?)>(
            svc => svc.DeleteImageAsync(oglasId, ids[1], majstorId));
        obrisana.Should().BeTrue();

        var (rezultat, greska, _) = await PromeniAsync(oglasId, majstorId, [ids[2], ids[0]]);

        greska.Should().BeNull();
        rezultat!.Select(s => s.SortOrder).Should().Equal(0, 1);
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids[2], ids[0]);
    }

    [Fact]
    public async Task OglasBezSlika_PraznaLista_Prolazi()
    {
        var (majstorId, oglasId, _) = await PostaviAsync(brojSlika: 0);

        var (rezultat, greska, _) = await PromeniAsync(oglasId, majstorId, []);

        greska.Should().BeNull();
        rezultat.Should().BeEmpty();
    }

    // ── Vlasništvo ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TudjOglas_NijePronadjen_RedosledOstaje()
    {
        var (_, oglasId, ids) = await PostaviAsync();
        var (stranac, _) = await Data.CreateProviderAsync("stranac@test.rs");

        var (rezultat, greska, nijePronadjeno) =
            await PromeniAsync(oglasId, stranac.Id, [ids[2], ids[1], ids[0]]);

        rezultat.Should().BeNull();
        greska.Should().NotBeNull();
        nijePronadjeno.Should().BeTrue("tuđ oglas se ponaša kao nepostojeći, kao kod brisanja slike");
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids);
    }

    [Fact]
    public async Task NepostojeciOglas_NijePronadjen()
    {
        var (majstorId, _, _) = await PostaviAsync(brojSlika: 0);

        var (_, greska, nijePronadjeno) = await PromeniAsync(999_999, majstorId, [1]);

        greska.Should().NotBeNull();
        nijePronadjeno.Should().BeTrue();
    }

    // ── Neispravna lista ───────────────────────────────────────────────────

    [Fact]
    public async Task NepotpunaLista_Odbija_RedosledOstaje()
    {
        // Delimična lista bi ostavila izostavljenu sliku sa starim SortOrder-om
        // — moguće istim kao neka od preuređenih.
        var (majstorId, oglasId, ids) = await PostaviAsync();

        var (rezultat, greska, nijePronadjeno) = await PromeniAsync(oglasId, majstorId, [ids[2], ids[0]]);

        rezultat.Should().BeNull();
        greska.Should().Contain("tačno sve slike");
        nijePronadjeno.Should().BeFalse("oglas postoji — zahtev je neispravan, 400 a ne 404");
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids);
    }

    [Fact]
    public async Task StranaSlika_Odbija_RedosledOstaje()
    {
        // Isti broj elemenata, ali jedan id pripada TUĐEM oglasu. Da se gleda
        // samo broj, ovo bi prošlo i upisalo SortOrder na tuđu sliku.
        var (majstorId, oglasId, ids) = await PostaviAsync();
        var (_, tudjOglasId, tudjeIds) = await PostaviAsync(brojSlika: 1, email: "drugi@test.rs");

        var (rezultat, greska, nijePronadjeno) =
            await PromeniAsync(oglasId, majstorId, [ids[0], ids[1], tudjeIds[0]]);

        rezultat.Should().BeNull();
        greska.Should().Contain("tačno sve slike");
        nijePronadjeno.Should().BeFalse();
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids);
        (await RedosledIzBazeAsync(tudjOglasId)).Should().Equal(tudjeIds);
    }

    [Fact]
    public async Task ViseIdJevaNegoSlika_Odbija()
    {
        var (majstorId, oglasId, ids) = await PostaviAsync();

        var (_, greska, nijePronadjeno) =
            await PromeniAsync(oglasId, majstorId, [ids[0], ids[1], ids[2], 999_999]);

        greska.Should().Contain("tačno sve slike");
        nijePronadjeno.Should().BeFalse();
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids);
    }

    [Fact]
    public async Task Duplikat_Odbija_RedosledOstaje()
    {
        // {a, a, b} ima isti broj elemenata kao {a, b, c}.
        var (majstorId, oglasId, ids) = await PostaviAsync();

        var (rezultat, greska, nijePronadjeno) =
            await PromeniAsync(oglasId, majstorId, [ids[1], ids[1], ids[0]]);

        rezultat.Should().BeNull();
        greska.Should().Contain("dva puta");
        nijePronadjeno.Should().BeFalse();
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids);
    }

    // ── Arhiva ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ArhiviranOglas_Odbija()
    {
        var (majstorId, oglasId, ids) = await PostaviAsync();

        var (arhiviran, _) = await WithService<ListingService, (bool, string?)>(
            svc => svc.DeleteAsync(oglasId, majstorId));
        arhiviran.Should().BeTrue();

        var (rezultat, greska, nijePronadjeno) =
            await PromeniAsync(oglasId, majstorId, [ids[2], ids[1], ids[0]]);

        rezultat.Should().BeNull();
        greska.Should().Contain("arhiviran", "ista poruka kao kod upload-a slike");
        nijePronadjeno.Should().BeFalse();
        (await RedosledIzBazeAsync(oglasId)).Should().Equal(ids);
    }
}
