using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravila automatske provere slika.
///
/// Zašto je vredno testa: provera se zove sa TRI mesta — slika oglasa, cover i
/// avatar — a nijedno ne deli kod sa ostalima. Četvrti upload put koji neko
/// doda sutra lako preskoči proveru, i to se ne vidi ni iz koda ni iz
/// ponašanja aplikacije. Zato ovde svako od tri mesta ima svoj test.
///
/// Srednji ishod (Review) je srž dizajna: kategorije „Depilacija", „Fitnes
/// trener" i „Plivanje — instruktor" legitimno imaju golu kožu na slikama.
/// Binarni filter bi ih odbijao, pa se sumnjiva slika PRIMA i šalje čoveku.
/// </summary>
public class ImageModerationTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// Najmanji validan JPEG: SOI marker, minimalno telo, EOI.
    ///
    /// Sadržaj mora proći ImageUploads.ValidateAsync pre nego što provera
    /// sadržaja uopšte dođe na red — format se utvrđuje iz prva tri bajta.
    /// </summary>
    private static IFormFile Slika(string ime = "test.jpg")
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46,
                       0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                       0xFF, 0xD9];

        var ms = new MemoryStream(jpeg);
        return new FormFile(ms, 0, ms.Length, "file", ime)
        {
            Headers     = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    private async Task<(string MajstorId, int OglasId)> PostaviAsync()
    {
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs");
        var oglasId = await Data.CreateActiveListingAsync(majstor.Id);
        return (majstor.Id, oglasId);
    }

    // ── Reject: sva tri puta moraju odbiti ─────────────────────────────────

    [Fact]
    public async Task Reject_OdbijaSlikuOglasa()
    {
        var (majstorId, oglasId) = await PostaviAsync();
        Factory.ImageModerator.Verdict = ImageVerdict.Reject;

        var (slika, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
            svc => svc.UploadImageAsync(oglasId, majstorId, Slika()));

        slika.Should().BeNull();
        greska.Should().NotBeNull();
        Factory.ImageModerator.BrojPoziva.Should().Be(1, "provera mora biti pozvana");
    }

    [Fact]
    public async Task Reject_OdbijaCoverSliku()
    {
        var (majstorId, _) = await PostaviAsync();
        Factory.ImageModerator.Verdict = ImageVerdict.Reject;

        var (url, greska) = await WithService<ProviderService, (string?, string?)>(
            svc => svc.UploadCoverAsync(majstorId, Slika()));

        url.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task Reject_OdbijaAvatar()
    {
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");
        Factory.ImageModerator.Verdict = ImageVerdict.Reject;

        var (url, greska) = await WithService<UserService, (string?, string?)>(
            svc => svc.UploadAvatarAsync(korisnik.Id, Slika()));

        url.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task Reject_PorukaNePominjeSadrzajSlike()
    {
        // Automatska provera greši. Poruka koja kaže „golotinja" pogađa i onoga
        // ko nije ništa prekršio — a njemu je to uvreda, ne objašnjenje.
        var (majstorId, oglasId) = await PostaviAsync();
        Factory.ImageModerator.Verdict = ImageVerdict.Reject;

        var (_, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
            svc => svc.UploadImageAsync(oglasId, majstorId, Slika()));

        greska.Should().NotBeNull();
        greska!.ToLowerInvariant().Should().NotContain("golot");
        greska!.ToLowerInvariant().Should().NotContain("nudity");
        greska!.ToLowerInvariant().Should().NotContain("adult");
    }

    // ── Review: slika prolazi, ali ostavlja prijavu ────────────────────────

    [Fact]
    public async Task Review_PrimaSlikuIPraviPrijavu()
    {
        // SRŽ DIZAJNA. Da je ovo Reject, depilacija i plivanje bi bili
        // neupotrebljivi kao kategorije.
        await Data.CreateAdminAsync();
        var (majstorId, oglasId) = await PostaviAsync();
        Factory.ImageModerator.Verdict = ImageVerdict.Review;

        var (slika, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
            svc => svc.UploadImageAsync(oglasId, majstorId, Slika()));

        slika.Should().NotBeNull(greska);

        var prijava = await Query(db => db.Reports.SingleOrDefaultAsync());
        prijava.Should().NotBeNull("sumnjiva slika mora ostaviti trag za ručni pregled");
        prijava!.ListingId.Should().Be(oglasId);
        prijava.Status.Should().Be(ReportStatus.Pending);
        prijava.Reason.Should().Be(ReportReason.Neprikladno);
    }

    [Fact]
    public async Task Review_NaAvataru_PrijavljujeKorisnikaNeOglas()
    {
        // Avatar ne pripada oglasu, pa je meta prijave korisnik. Da se
        // prijavljivao oglas, admin bi dobio metu koja nema veze sa slikom.
        await Data.CreateAdminAsync();
        var korisnik = await Data.CreateConfirmedUserAsync("korisnik@test.rs");
        Factory.ImageModerator.Verdict = ImageVerdict.Review;

        var (url, _) = await WithService<UserService, (string?, string?)>(
            svc => svc.UploadAvatarAsync(korisnik.Id, Slika()));

        url.Should().NotBeNull();

        var prijava = await Query(db => db.Reports.SingleAsync());
        prijava.TargetType.Should().Be(ReportTargetType.User);
        prijava.ReportedUserId.Should().Be(korisnik.Id);
        prijava.ListingId.Should().BeNull();
    }

    [Fact]
    public async Task Review_ViseSlikaIstogOglasa_PraviSamoJednuPrijavu()
    {
        // Oglas prima do pet slika. Bez provere „već prijavljeno", pet sumnjivih
        // slika bi napravilo pet prijava istog oglasa — a to bi ga u redu za
        // admina lažno diglo na vrh, iznad oglasa koje su prijavili ljudi.
        //
        // Filtrirani UNIQUE indeks bi ionako odbio drugu prijavu, ali kroz
        // izuzetak; ovde se proverava unapred.
        await Data.CreateAdminAsync();
        var (majstorId, oglasId) = await PostaviAsync();
        Factory.ImageModerator.Verdict = ImageVerdict.Review;

        await WithService<ListingService>(svc => svc.UploadImageAsync(oglasId, majstorId, Slika("a.jpg")));
        await WithService<ListingService>(svc => svc.UploadImageAsync(oglasId, majstorId, Slika("b.jpg")));
        await WithService<ListingService>(svc => svc.UploadImageAsync(oglasId, majstorId, Slika("c.jpg")));

        (await Query(db => db.Reports.CountAsync())).Should().Be(1);
        (await Query(db => db.ListingImages.CountAsync(i => i.ListingId == oglasId)))
            .Should().Be(3, "sve tri slike su primljene");
    }

    // ── Allow i fail-open ──────────────────────────────────────────────────

    [Fact]
    public async Task Allow_PropustaSlikuBezPrijave()
    {
        // POZITIVNA KONTROLA. Bez nje bi i kod koji SVAKU sliku prijavljuje
        // prošao testove iznad.
        await Data.CreateAdminAsync();
        var (majstorId, oglasId) = await PostaviAsync();
        Factory.ImageModerator.Verdict = ImageVerdict.Allow;

        var (slika, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
            svc => svc.UploadImageAsync(oglasId, majstorId, Slika()));

        slika.Should().NotBeNull(greska);
        (await Query(db => db.Reports.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task PadProvere_NeSmeDaOboriUpload()
    {
        // FAIL-OPEN. Pad tuđeg servisa ne sme da obori postavljanje oglasa na
        // celoj aplikaciji — isti princip kao AbortOnConnectFail=false kod Redisa.
        //
        // Izuzetak ovde leti iz SAME provere, pa ga mora uhvatiti
        // ImageModerationGate. CloudVisionImageModerator ima i sopstveni catch,
        // ali on štiti samo tu implementaciju; ovaj test proverava ugovor koji
        // važi za svaki IImageModerator.
        await Data.CreateAdminAsync();
        var (majstorId, oglasId) = await PostaviAsync();
        Factory.ImageModerator.BaciGresku = new HttpRequestException("servis nedostupan");

        var (slika, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
            svc => svc.UploadImageAsync(oglasId, majstorId, Slika()));

        slika.Should().NotBeNull(greska ?? "upload mora proći i kad provera padne");

        // Ali NE tiho: sumnjiva slika koja prođe zbog pada provere mora ostaviti
        // trag, inače bi ispad servisa bio rupa kroz koju sadržaj prolazi bez
        // ijednog pregleda.
        var prijava = await Query(db => db.Reports.SingleOrDefaultAsync());
        prijava.Should().NotBeNull();
        prijava!.Note.Should().Contain("HttpRequestException");
    }
}
