using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravila sistema prijava.
///
/// Zašto je vredno testa: sistem prijava je i sam vektor napada. Ako jedan
/// korisnik može da prijavi isti oglas deset puta, sam sebi diže metu na vrh
/// reda i gura stvarne prekršaje niže. Ako admin reši jednu prijavu a ostale
/// ostanu otvorene, ista meta se vraća u red bez novog povoda i posao se
/// ponavlja u krug.
///
/// Rangiranje je posebno vredno: red poređan samo po broju prijava izgleda
/// ispravno dok se ne pojavi usamljena stara prijava — koja onda nikad ne
/// stigne na red, iako baš ona može biti tačna.
/// </summary>
public class ReportTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<(string PrijavilacId, string MajstorId, int OglasId)> PostaviAsync()
    {
        var prijavilac = await Data.CreateConfirmedUserAsync("prijavilac@test.rs", "Prijavilac Testić");
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs", fullName: "Majstor Testić");
        var oglasId = await Data.CreateActiveListingAsync(majstor.Id, "Sumnjiv oglas");

        return (prijavilac.Id, majstor.Id, oglasId);
    }

    private static CreateReportDto PrijavaOglasa(
        int oglasId, ReportReason razlog = ReportReason.Neprikladno) => new()
    {
        TargetType = ReportTargetType.Listing,
        ListingId  = oglasId,
        Reason     = razlog,
        Note       = "Napomena prijavioca"
    };

    private Task<(bool, string?)> PrijaviAsync(string ko, CreateReportDto dto) =>
        WithService<ReportService, (bool, string?)>(svc => svc.PrijaviAsync(ko, dto));

    // ── Osnovni tok ────────────────────────────────────────────────────────

    [Fact]
    public async Task Prijava_PraviNeresenRed()
    {
        var (prijavilacId, _, oglasId) = await PostaviAsync();

        var (uspeh, greska) = await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));

        uspeh.Should().BeTrue(greska);

        var prijava = await Query(db => db.Reports.SingleAsync());
        prijava.Status.Should().Be(ReportStatus.Pending);
        prijava.ListingId.Should().Be(oglasId);
        prijava.ReportedUserId.Should().BeNull("meta je oglas, ne korisnik");
    }

    [Fact]
    public async Task Prijava_SopstvenogOglasa_Odbijena()
    {
        var (_, majstorId, oglasId) = await PostaviAsync();

        var (uspeh, greska) = await PrijaviAsync(majstorId, PrijavaOglasa(oglasId));

        uspeh.Should().BeFalse();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task Prijava_BezMete_Odbijena()
    {
        // Pravilo koje čuva i CHECK ograničenje u bazi, samo ranije i sa
        // razumljivom porukom. Bez provere u servisu korisnik dobija 500 iz SQL-a.
        var (prijavilacId, _, _) = await PostaviAsync();

        var (uspeh, greska) = await PrijaviAsync(prijavilacId, new CreateReportDto
        {
            TargetType = ReportTargetType.Listing,
            Reason     = ReportReason.Spam
        });

        uspeh.Should().BeFalse();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task Prijava_SaObeMete_Odbijena()
    {
        // Druga polovina istog pravila. Bez nje bi red sa obe popunjene mete
        // prošao servis i pao tek na CHECK ograničenju — ili, gore, prošao i
        // ostavio prijavu koju admin ne može da reši jer ne zna šta uklanja.
        var (prijavilacId, majstorId, oglasId) = await PostaviAsync();

        var (uspeh, _) = await PrijaviAsync(prijavilacId, new CreateReportDto
        {
            TargetType     = ReportTargetType.Listing,
            ListingId      = oglasId,
            ReportedUserId = majstorId,
            Reason         = ReportReason.Spam
        });

        uspeh.Should().BeFalse();
    }

    // ── Zaštita od zloupotrebe ─────────────────────────────────────────────

    [Fact]
    public async Task DrugaPrijava_IstogKorisnika_IsteMete_Odbijena()
    {
        // Bez ovoga jedan nalog uzastopnim prijavama sam diže metu na vrh reda
        // i gura stvarne prekršaje niže.
        var (prijavilacId, _, oglasId) = await PostaviAsync();

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));
        var (uspeh, greska) = await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId, ReportReason.Spam));

        uspeh.Should().BeFalse();
        greska.Should().NotBeNull();

        (await Query(db => db.Reports.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task DvaRazlicitaKorisnika_MoguPrijavitiIstiOglas()
    {
        // Suprotna strana istog pravila. Filtrirani UNIQUE indeks je lako
        // napisati prekruto — na (ListingId) umesto na (ReporterId, ListingId) —
        // i time dozvoliti samo JEDNU prijavu po oglasu ikada. Tada rangiranje
        // po broju prijava nema šta da broji.
        var (prijavilacId, _, oglasId) = await PostaviAsync();
        var drugi = await Data.CreateConfirmedUserAsync("drugi@test.rs");

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));
        var (uspeh, greska) = await PrijaviAsync(drugi.Id, PrijavaOglasa(oglasId));

        uspeh.Should().BeTrue(greska);
        (await Query(db => db.Reports.CountAsync())).Should().Be(2);
    }

    // ── Rangiranje ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Red_SortiranPoBrojuPrijava()
    {
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs");
        var maloPrijava  = await Data.CreateActiveListingAsync(majstor.Id, "Jedna prijava");
        var punoPrijava  = await Data.CreateActiveListingAsync(majstor.Id, "Tri prijave");

        var a = await Data.CreateConfirmedUserAsync("a@test.rs");
        var b = await Data.CreateConfirmedUserAsync("b@test.rs");
        var c = await Data.CreateConfirmedUserAsync("c@test.rs");

        await PrijaviAsync(a.Id, PrijavaOglasa(maloPrijava));

        await PrijaviAsync(a.Id, PrijavaOglasa(punoPrijava));
        await PrijaviAsync(b.Id, PrijavaOglasa(punoPrijava));
        await PrijaviAsync(c.Id, PrijavaOglasa(punoPrijava));

        var red = await WithService<ReportService, List<ReportQueueItemDto>>(
            svc => svc.RedAsync());

        red.Should().HaveCount(2);
        red[0].ListingId.Should().Be(punoPrijava);
        red[0].BrojPrijava.Should().Be(3);
        red[1].ListingId.Should().Be(maloPrijava);
    }

    [Fact]
    public async Task Red_PriIstomBrojuPrijava_StarijaMetaIdePrva()
    {
        // Drugi kriterijum sortiranja. Bez njega bi usamljena stara prijava
        // nikad ne stigla na red — a upravo ona može biti tačna.
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs");
        var stariji = await Data.CreateActiveListingAsync(majstor.Id, "Stariji");
        var noviji  = await Data.CreateActiveListingAsync(majstor.Id, "Noviji");

        var a = await Data.CreateConfirmedUserAsync("a@test.rs");

        await PrijaviAsync(a.Id, PrijavaOglasa(stariji));

        // Prijave se sortiraju po vremenu, pa se starija mora i dogoditi ranije.
        await Task.Delay(1100);
        await PrijaviAsync(a.Id, PrijavaOglasa(noviji));

        var red = await WithService<ReportService, List<ReportQueueItemDto>>(
            svc => svc.RedAsync());

        red.Should().HaveCount(2);
        red[0].ListingId.Should().Be(stariji);
    }

    [Fact]
    public async Task Red_PrikazujeVlasnikaIRazloge()
    {
        // Admin mora da vidi ŠTA gleda pre nego što odluči. Red koji prikazuje
        // samo id-eve tera ga da otvara oglas po oglas.
        var (prijavilacId, majstorId, oglasId) = await PostaviAsync();

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId, ReportReason.Prevara));

        var red = await WithService<ReportService, List<ReportQueueItemDto>>(
            svc => svc.RedAsync());

        red.Should().ContainSingle();
        red[0].TargetNaziv.Should().Be("Sumnjiv oglas");
        red[0].VlasnikId.Should().Be(majstorId);
        red[0].Razlozi.Should().Contain(nameof(ReportReason.Prevara));
    }

    // ── Rešavanje ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Prihvatanje_ArhiviraOglasIZatvaraSvePrijaveZaNjega()
    {
        // Jedna odluka zatvara SVE prijave te mete. Da ostanu otvorene, ista
        // meta bi se vraćala u red bez novog povoda.
        var (prijavilacId, _, oglasId) = await PostaviAsync();
        var drugi = await Data.CreateConfirmedUserAsync("drugi@test.rs");
        var admin = await Data.CreateConfirmedUserAsync("admin2@test.rs");

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));
        await PrijaviAsync(drugi.Id,     PrijavaOglasa(oglasId));

        var (uspeh, greska) = await WithService<ReportService, (bool, string?)>(
            svc => svc.ResiAsync(admin.Id, new ResolveReportDto
            {
                TargetType = ReportTargetType.Listing,
                ListingId  = oglasId,
                Action     = ReportAction.UkloniOglas,
                Note       = "Krši uslove"
            }));

        uspeh.Should().BeTrue(greska);

        var oglas = await Query(db => db.Listings.SingleAsync(l => l.Id == oglasId));
        oglas.Status.Should().Be(ListingStatus.Archived);
        oglas.ModerationState.Should().Be(ModerationState.Removed);

        var prijave = await Query(db => db.Reports.ToListAsync());
        prijave.Should().HaveCount(2);
        prijave.Should().OnlyContain(r => r.Status == ReportStatus.Accepted);
        prijave.Should().OnlyContain(r => r.ResolvedById == admin.Id);
    }

    [Fact]
    public async Task Odbijanje_OstavljaOglasIObelezavaGaKaoPregledan()
    {
        var (prijavilacId, _, oglasId) = await PostaviAsync();
        var admin = await Data.CreateConfirmedUserAsync("admin2@test.rs");

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));

        await WithService<ReportService>(svc => svc.ResiAsync(admin.Id, new ResolveReportDto
        {
            TargetType = ReportTargetType.Listing,
            ListingId  = oglasId,
            Action     = ReportAction.Odbij
        }));

        var oglas = await Query(db => db.Listings.SingleAsync(l => l.Id == oglasId));
        oglas.Status.Should().Be(ListingStatus.Active, "odbijena prijava ne dira sadržaj");
        oglas.ModerationState.Should().Be(ModerationState.Cleared);

        (await Query(db => db.Reports.SingleAsync())).Status.Should().Be(ReportStatus.Rejected);
    }

    [Fact]
    public async Task Resavanje_KadNemaNeresenihPrijava_Odbijeno()
    {
        var (_, _, oglasId) = await PostaviAsync();
        var admin = await Data.CreateConfirmedUserAsync("admin2@test.rs");

        var (uspeh, greska) = await WithService<ReportService, (bool, string?)>(
            svc => svc.ResiAsync(admin.Id, new ResolveReportDto
            {
                TargetType = ReportTargetType.Listing,
                ListingId  = oglasId,
                Action     = ReportAction.UkloniOglas
            }));

        uspeh.Should().BeFalse();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task PrijavaPrezivljavaUklanjanjeOglasa()
    {
        // Revizijski trag je ceo smisao evidencije: bez njega se posle ne može
        // utvrditi ZAŠTO je nešto uklonjeno ni ko je odlučio.
        var (prijavilacId, _, oglasId) = await PostaviAsync();
        var admin = await Data.CreateConfirmedUserAsync("admin2@test.rs");

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));

        await WithService<ReportService>(svc => svc.ResiAsync(admin.Id, new ResolveReportDto
        {
            TargetType = ReportTargetType.Listing,
            ListingId  = oglasId,
            Action     = ReportAction.UkloniOglas,
            Note       = "Neprikladan sadržaj"
        }));

        var prijava = await Query(db => db.Reports.SingleAsync());
        prijava.ListingId.Should().Be(oglasId);
        prijava.ResolutionNote.Should().Be("Neprikladan sadržaj");
        prijava.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ReseneMete_NisuViseUReduZaPregled()
    {
        var (prijavilacId, _, oglasId) = await PostaviAsync();
        var admin = await Data.CreateConfirmedUserAsync("admin2@test.rs");

        await PrijaviAsync(prijavilacId, PrijavaOglasa(oglasId));
        await WithService<ReportService>(svc => svc.ResiAsync(admin.Id, new ResolveReportDto
        {
            TargetType = ReportTargetType.Listing,
            ListingId  = oglasId,
            Action     = ReportAction.Odbij
        }));

        var red = await WithService<ReportService, List<ReportQueueItemDto>>(
            svc => svc.RedAsync());

        red.Should().BeEmpty();
        (await WithService<ReportService, int>(svc => svc.BrojNeresenihAsync())).Should().Be(0);
    }
}
