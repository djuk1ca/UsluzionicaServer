using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo: deaktivacija naloga povlači SVE svoje posledice.
///
/// Zašto je vredno testa: pre ovoga je <c>IsActive</c> proveravan samo pri
/// prijavi i osvežavanju tokena. Nalog se gasio, a njegovi oglasi su ostajali u
/// pretrazi i refresh tokeni su i dalje važili. Praktično: admin reaguje na
/// prijavu, a sadržaj zbog kog je reagovao ostaje na ekranu.
///
/// Takva greška se ne vidi iz koda koji je pravi — deaktivacija „radi", jer
/// korisnik zaista ne može da se prijavi. Vidi se samo ako se proveri šta se
/// desilo sa njegovim sadržajem.
/// </summary>
public class ModerationCleanupTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private async Task<(string MajstorId, int OglasId)> PostaviAsync()
    {
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs");
        var oglasId = await Data.CreateActiveListingAsync(majstor.Id, "Popravka slavine");
        return (majstor.Id, oglasId);
    }

    [Fact]
    public async Task Deaktivacija_SklanjaOglaseIzPretrage()
    {
        // NAJVAŽNIJI TEST U KLASI — pokriva rupu koja je i navela na ceo posao.
        var (majstorId, oglasId) = await PostaviAsync();

        var preDeaktivacije = await WithService<ListingService, PagedResult<ListingDto>>(
            svc => svc.SearchAsync(new ListingQueryParams()));
        preDeaktivacije.Items.Should().Contain(l => l.Id == oglasId,
            "pozitivna kontrola — oglas mora biti vidljiv pre deaktivacije");

        await WithService<UserModerationService>(
            svc => svc.DeaktivirajAsync(majstorId, "Kršenje uslova"));

        var posle = await WithService<ListingService, PagedResult<ListingDto>>(
            svc => svc.SearchAsync(new ListingQueryParams()));

        posle.Items.Should().NotContain(l => l.Id == oglasId);
    }

    [Fact]
    public async Task Deaktivacija_ArhiviraOglase()
    {
        var (majstorId, oglasId) = await PostaviAsync();

        await WithService<UserModerationService>(svc => svc.DeaktivirajAsync(majstorId));

        var oglas = await Query(db => db.Listings.SingleAsync(l => l.Id == oglasId));
        oglas.Status.Should().Be(ListingStatus.Archived);
    }

    [Fact]
    public async Task Deaktivacija_PonistavaRefreshTokene()
    {
        // Access token traje 60 minuta i ne može se opozvati, ali bez ovoga bi
        // banovan korisnik mogao da ga produžava unedogled.
        var (majstorId, _) = await PostaviAsync();

        await Query(async db =>
        {
            db.RefreshTokens.Add(new UsluzionicaServer.Domain.Entities.RefreshToken
            {
                UserId    = majstorId,
                Token     = "test-token-koji-mora-biti-ponisten",
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                IsRevoked = false
            });
            return await db.SaveChangesAsync();
        });

        await WithService<UserModerationService>(svc => svc.DeaktivirajAsync(majstorId));

        var tokeni = await Query(db => db.RefreshTokens
            .Where(t => t.UserId == majstorId)
            .ToListAsync());

        tokeni.Should().NotBeEmpty("test bi bio bezvredan da token nije ni nastao");
        tokeni.Should().OnlyContain(t => t.IsRevoked);
    }

    [Fact]
    public async Task Deaktivacija_ZatvaraDetaljeOglasaZaDruge()
    {
        var (majstorId, oglasId) = await PostaviAsync();
        var posetilac = await Data.CreateConfirmedUserAsync("posetilac@test.rs");

        await WithService<UserModerationService>(svc => svc.DeaktivirajAsync(majstorId));

        var oglas = await WithService<ListingService, ListingDto?>(
            svc => svc.GetByIdAsync(oglasId, posetilac.Id));

        oglas.Should().BeNull();
    }

    [Fact]
    public async Task Deaktivacija_VlasnikIDaljeVidiSvojOglas()
    {
        // Bez ovoga korisnik ne bi znao šta mu se desilo sa sadržajem niti
        // mogao da ga ispravi pre žalbe.
        var (majstorId, oglasId) = await PostaviAsync();

        await WithService<UserModerationService>(svc => svc.DeaktivirajAsync(majstorId));

        var oglas = await WithService<ListingService, ListingDto?>(
            svc => svc.GetByIdAsync(oglasId, majstorId));

        oglas.Should().NotBeNull();
    }

    [Fact]
    public async Task PonovljenaDeaktivacija_NemaDodatnihPosledica()
    {
        // Uslovni UPDATE: druga deaktivacija vraća 0 promenjenih redova i
        // preskače ostatak. Bez toga bi korisnik dobio drugu notifikaciju o
        // gašenju naloga koji je već ugašen.
        var (majstorId, _) = await PostaviAsync();

        await WithService<UserModerationService>(svc => svc.DeaktivirajAsync(majstorId));
        var (uspeh, greska) = await WithService<UserModerationService, (bool, string?)>(
            svc => svc.DeaktivirajAsync(majstorId));

        uspeh.Should().BeTrue("traženo stanje već postoji — to nije greška");
        greska.Should().BeNull();

        var notifikacije = await Query(db => db.Notifications
            .CountAsync(n => n.UserId == majstorId &&
                             n.Kind   == NotificationKind.AccountDeactivated));

        notifikacije.Should().Be(1);
    }

    [Fact]
    public async Task Vracanje_NeVracaOglaseIzArhive()
    {
        // NAMERNO ponašanje, ne propust.
        //
        // U trenutku vraćanja se ne zna koji su oglasi arhivirani zbog kazne, a
        // koje je vlasnik sam arhivirao ranije. Automatsko vraćanje bi mu
        // oživelo oglase koje je svesno sklonio.
        var (majstorId, oglasId) = await PostaviAsync();

        await WithService<UserModerationService>(svc => svc.DeaktivirajAsync(majstorId));
        await WithService<UserModerationService>(svc => svc.VratiAsync(majstorId));

        var korisnik = await Query(db => db.Users.SingleAsync(u => u.Id == majstorId));
        korisnik.IsActive.Should().BeTrue();

        var oglas = await Query(db => db.Listings.SingleAsync(l => l.Id == oglasId));
        oglas.Status.Should().Be(ListingStatus.Archived,
            "vlasnik sam vraća svoje oglase, kroz „Moje oglase\"");
    }
}
