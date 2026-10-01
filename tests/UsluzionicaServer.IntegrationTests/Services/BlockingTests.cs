using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo: blokada važi SIMETRIČNO i na svakom putu do sadržaja.
///
/// Zašto je vredno testa: blokada nije jedna provera na jednom mestu nego isto
/// pravilo primenjeno na petnaestak upita — pretraga, detalji oglasa, profil,
/// razgovori, poruke, recenzije, rezervacije, omiljeni. Propusti se na jednom
/// mestu i blokada i dalje „radi" svuda gde je korisnik gleda, a curi tačno na
/// onom putu koji niko nije proverio.
///
/// Simetrija je posebno lako pokvariti: intuitivno je napisati „A ne vidi B" i
/// stati tu. Tada B nastavlja da gleda A-ove oglase i da mu piše — a to je
/// upravo ono od čega se A sklonio.
/// </summary>
public class BlockingTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // ── Postavka koja se ponavlja ──────────────────────────────────────────

    /// <summary>Uslugodavac sa jednim aktivnim oglasom + klijent koji gleda.</summary>
    private async Task<(string KlijentId, string MajstorId, int OglasId)> PostaviAsync()
    {
        var klijent = await Data.CreateConfirmedUserAsync("klijent@test.rs", "Klijent Testić");
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs", fullName: "Majstor Testić");
        var oglasId = await Data.CreateActiveListingAsync(majstor.Id, "Popravka slavine");

        return (klijent.Id, majstor.Id, oglasId);
    }

    /// <summary>
    /// Blokira i PROVERAVA da je blokada prošla.
    ///
    /// Ranije se rezultat ignorisao. Test koji očekuje da nešto NIJE vidljivo
    /// tada prolazi i kad je blokiranje palo, ako oglas nestane iz nekog drugog
    /// razloga — a pad blokiranja je upravo kvar koji ovi testovi treba da uhvate.
    /// </summary>
    private async Task BlokirajAsync(string ko, string koga)
    {
        var (uspeh, greska) = await WithService<BlockService, (bool, string?)>(
            svc => svc.BlokirajAsync(ko, koga));

        uspeh.Should().BeTrue(greska ?? string.Empty);
    }

    private Task<ProviderProfileDto?> ProfilAsync(int profilId, string? gledalacId) =>
        WithService<ProviderService, ProviderProfileDto?>(
            svc => svc.GetPublicProfileAsync(profilId, gledalacId));

    private Task<PagedResult<ListingDto>> PretraziAsync(string? gledalacId) =>
        WithService<ListingService, PagedResult<ListingDto>>(
            svc => svc.SearchAsync(new ListingQueryParams { ViewerUserId = gledalacId }));

    // ── Pretraga ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_PreBlokade_VidiOglas()
    {
        // POZITIVNA KONTROLA. Bez nje bi i kod koji SVE oglase skriva prošao
        // testove ispod — a oni bi izgledali kao da blokada radi.
        var (klijentId, _, oglasId) = await PostaviAsync();

        var rezultat = await PretraziAsync(klijentId);

        rezultat.Items.Should().ContainSingle(l => l.Id == oglasId);
    }

    [Fact]
    public async Task Pretraga_KadKlijentBlokiraMajstora_NeVidiNjegovOglas()
    {
        var (klijentId, majstorId, oglasId) = await PostaviAsync();

        await BlokirajAsync(klijentId, majstorId);

        var rezultat = await PretraziAsync(klijentId);

        rezultat.Items.Should().NotContain(l => l.Id == oglasId);
    }

    [Fact]
    public async Task Pretraga_KadMajstorBlokiraKlijenta_KlijentTakodjeNeVidiOglas()
    {
        // OVO JE TEST SIMETRIJE, i najvredniji u klasi.
        //
        // Blokira MAJSTOR, a proverava se šta vidi KLIJENT. Naivna implementacija
        // („ne prikazuj ono što sam ja blokirao") prolazi test iznad a pada ovde,
        // jer klijent nije blokirao nikoga.
        var (klijentId, majstorId, oglasId) = await PostaviAsync();

        await BlokirajAsync(majstorId, klijentId);

        var rezultat = await PretraziAsync(klijentId);

        rezultat.Items.Should().NotContain(l => l.Id == oglasId);
    }

    [Fact]
    public async Task Pretraga_AnonimniPosetilac_VidiSve()
    {
        // Bez prijave nema blokada ni u jednom smeru, pa se filter ne primenjuje.
        //
        // Čuva i od suprotne greške: da NOT EXISTS sa null gledaocem slučajno
        // ne pogodi sve redove i isprazni pretragu neprijavljenima — a oni su
        // prvi koje aplikacija dočeka.
        var (klijentId, majstorId, oglasId) = await PostaviAsync();
        await BlokirajAsync(klijentId, majstorId);

        var rezultat = await PretraziAsync(gledalacId: null);

        rezultat.Items.Should().Contain(l => l.Id == oglasId);
    }

    // ── Direktan pristup oglasu ────────────────────────────────────────────

    [Fact]
    public async Task DetaljiOglasa_KadJeBlokiran_VracajuNull()
    {
        // Filter u pretrazi nije dovoljan: link na oglas ostaje u starom
        // razgovoru, u omiljenima, u istoriji pregledača. Da je zatvorena samo
        // pretraga, blokada bi značila „ne vidim te u listi", a direktan link
        // bi i dalje radio.
        var (klijentId, majstorId, oglasId) = await PostaviAsync();

        await BlokirajAsync(klijentId, majstorId);

        var oglas = await WithService<ListingService, ListingDto?>(
            svc => svc.GetByIdAsync(oglasId, klijentId));

        oglas.Should().BeNull();
    }

    [Fact]
    public async Task DetaljiOglasa_VlasnikUvekVidiSvoj()
    {
        // Vlasnik mora da vidi svoj oglas bez obzira na blokade — inače ne bi
        // znao šta mu se dešava sa sadržajem niti mogao da ga izmeni.
        var (klijentId, majstorId, oglasId) = await PostaviAsync();

        await BlokirajAsync(klijentId, majstorId);

        var oglas = await WithService<ListingService, ListingDto?>(
            svc => svc.GetByIdAsync(oglasId, majstorId));

        oglas.Should().NotBeNull();
    }

    // ── Profil uslugodavca ─────────────────────────────────────────────────

    [Fact]
    public async Task Profil_PreBlokade_Vidljiv()
    {
        // Pozitivna kontrola za dva testa ispod.
        var klijent      = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        var (_, profilId) = await Data.CreateProviderAsync("majstor@test.rs");

        (await ProfilAsync(profilId, klijent.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Profil_KadKlijentBlokiraMajstora_Sakriven()
    {
        var klijent             = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        var (majstor, profilId) = await Data.CreateProviderAsync("majstor@test.rs");

        await BlokirajAsync(klijent.Id, majstor.Id);

        (await ProfilAsync(profilId, klijent.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Profil_KadMajstorBlokiraKlijenta_KlijentGaTakodjeNeVidi()
    {
        // Simetrija na profilu: blokirani ne sme da gleda profil onoga ko ga je
        // blokirao. To je bio tačan opis prijavljenog kvara.
        var klijent             = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        var (majstor, profilId) = await Data.CreateProviderAsync("majstor@test.rs");

        await BlokirajAsync(majstor.Id, klijent.Id);

        (await ProfilAsync(profilId, klijent.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Profil_KesiranPreBlokade_IpakSakrivenPosle()
    {
        // Profil se kešira u Redisu. Provera blokade mora da stoji POSLE keša —
        // inače bi profil otvoren pre blokade još pet minuta bio vidljiv svima,
        // uključujući onoga ko je upravo blokiran.
        var klijent             = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        var (majstor, profilId) = await Data.CreateProviderAsync("majstor@test.rs");

        (await ProfilAsync(profilId, klijent.Id)).Should().NotBeNull("prvi poziv puni keš");

        await BlokirajAsync(majstor.Id, klijent.Id);

        (await ProfilAsync(profilId, klijent.Id)).Should().BeNull();
    }

    [Fact]
    public async Task OglasiNaProfilu_KadJeBlokiran_Prazni()
    {
        var klijent             = await Data.CreateConfirmedUserAsync("klijent@test.rs");
        var (majstor, profilId) = await Data.CreateProviderAsync("majstor@test.rs");
        await Data.CreateActiveListingAsync(majstor.Id);

        await BlokirajAsync(majstor.Id, klijent.Id);

        var oglasi = await WithService<ProviderService, List<ListingDto>>(
            svc => svc.GetProviderListingsAsync(profilId, klijent.Id));

        oglasi.Should().BeEmpty();
    }

    // ── Poruke ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OtvaranjeRazgovora_SaBlokiranim_Odbijeno()
    {
        var (klijentId, majstorId, _) = await PostaviAsync();

        await BlokirajAsync(klijentId, majstorId);

        var (razgovor, greska) = await WithService<ConversationService, (ConversationDto?, string?)>(
            svc => svc.GetOrCreateAsync(klijentId, majstorId));

        razgovor.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task SlanjePoruke_KrozRAZGOVOR_OTVOREN_PRE_BLOKADE_Odbijeno()
    {
        // NAJVAŽNIJI SLUČAJ U OVOJ GRUPI.
        //
        // Razgovor je nastao dok blokade nije bilo, pa klijent i dalje drži
        // njegov id. Provera samo u GetOrCreateAsync ovde ne pomaže — poruka
        // ide direktno u postojeći razgovor. Bez provere u SendMessageAsync,
        // blokirani nastavlja da piše baš tamo gde je i ranije pisao.
        var (klijentId, majstorId, _) = await PostaviAsync();

        var (razgovor, _) = await WithService<ConversationService, (ConversationDto?, string?)>(
            svc => svc.GetOrCreateAsync(klijentId, majstorId));

        razgovor.Should().NotBeNull("razgovor mora nastati PRE blokade");

        await BlokirajAsync(klijentId, majstorId);

        var (poruka, greska) = await WithService<ConversationService, (MessageDto?, string?)>(
            svc => svc.SendMessageAsync(razgovor!.Id, klijentId, "Halo?"));

        poruka.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task IstorijaPoruka_DokTrajeBlokada_Nedostupna()
    {
        // Id razgovora preživljava blokadu — u starom obaveštenju, u push
        // notifikaciji, u ekranu koji je već bio otvoren. Lista razgovora je
        // filtrirana, ali bez provere ovde taj put i dalje otvara ceo razgovor.
        //
        // Proverava se strana koja NIJE blokirala, jer je to smer koji se lako
        // zaboravi.
        var (klijentId, majstorId, _) = await PostaviAsync();

        var (razgovor, _) = await WithService<ConversationService, (ConversationDto?, string?)>(
            svc => svc.GetOrCreateAsync(klijentId, majstorId));

        await WithService<ConversationService>(
            svc => svc.SendMessageAsync(razgovor!.Id, klijentId, "Dobar dan"));

        await BlokirajAsync(klijentId, majstorId);

        var (poruke, greska) = await WithService<ConversationService, (List<MessageDto>?, string?)>(
            svc => svc.GetMessagesAsync(razgovor!.Id, majstorId));

        poruke.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task ListaRazgovora_SakrivaBlokiranogSaObeStrane()
    {
        var (klijentId, majstorId, _) = await PostaviAsync();

        await WithService<ConversationService>(
            svc => svc.GetOrCreateAsync(klijentId, majstorId));

        await BlokirajAsync(klijentId, majstorId);

        var kodKlijenta = await WithService<ConversationService, List<ConversationDto>>(
            svc => svc.GetConversationsAsync(klijentId));
        var kodMajstora = await WithService<ConversationService, List<ConversationDto>>(
            svc => svc.GetConversationsAsync(majstorId));

        kodKlijenta.Should().BeEmpty();
        kodMajstora.Should().BeEmpty("blokada je simetrična — nestaje i onome ko nije blokirao");
    }

    [Fact]
    public async Task Deblokiranje_VracaRazgovorSaIstorijom()
    {
        // Razgovor se skriva, ne briše. Ovaj test je jedina odbrana od
        // „optimizacije" koja bi ga pri blokiranju obrisala — posle čega bi
        // deblokiranje vraćalo praznu listu, a poruke bile nepovratno izgubljene.
        var (klijentId, majstorId, _) = await PostaviAsync();

        var (razgovor, _) = await WithService<ConversationService, (ConversationDto?, string?)>(
            svc => svc.GetOrCreateAsync(klijentId, majstorId));

        await WithService<ConversationService>(
            svc => svc.SendMessageAsync(razgovor!.Id, klijentId, "Dobar dan"));

        await BlokirajAsync(klijentId, majstorId);
        await WithService<BlockService>(svc => svc.OdblokirajAsync(klijentId, majstorId));

        var lista = await WithService<ConversationService, List<ConversationDto>>(
            svc => svc.GetConversationsAsync(klijentId));

        lista.Should().ContainSingle(c => c.Id == razgovor!.Id);

        var (poruke, _) = await WithService<ConversationService, (List<MessageDto>?, string?)>(
            svc => svc.GetMessagesAsync(razgovor!.Id, klijentId));

        poruke.Should().NotBeNullOrEmpty();
    }

    // ── Recenzije ──────────────────────────────────────────────────────────

    [Fact]
    public async Task PisanjeRecenzije_BlokiranomUslugodavcu_Odbijeno()
    {
        // Bez ove provere recenzija ostaje jedini kanal kojim blokirani dopire
        // do onoga ko ga je blokirao — i to javno, sa ocenom koja obara prosek.
        var (klijentId, majstorId, oglasId) = await PostaviAsync();

        await BlokirajAsync(majstorId, klijentId);

        var (recenzija, greska) = await WithService<ReviewService, (ReviewDto?, string?)>(
            svc => svc.CreateAsync(klijentId, new CreateReviewDto
            {
                ListingId = oglasId,
                Stars     = 1,
                Comment   = "Nezadovoljan"
            }));

        recenzija.Should().BeNull();
        greska.Should().NotBeNull();
    }

    // ── Pravila samog blokiranja ───────────────────────────────────────────

    [Fact]
    public async Task BlokiranjeSebe_Odbijeno()
    {
        var klijent = await Data.CreateConfirmedUserAsync("sam@test.rs");

        var (uspeh, greska) = await WithService<BlockService, (bool, string?)>(
            svc => svc.BlokirajAsync(klijent.Id, klijent.Id));

        uspeh.Should().BeFalse();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task DvostrukoBlokiranje_JeIdempotentno()
    {
        // Klijent sme da pošalje isti zahtev dvaput — dupli tap, ponovni pokušaj
        // posle prekida veze. Drugi poziv mora biti uspeh, ne crvena poruka, i
        // ne sme napraviti drugi red.
        var (klijentId, majstorId, _) = await PostaviAsync();

        await BlokirajAsync(klijentId, majstorId);

        var (uspeh, greska) = await WithService<BlockService, (bool, string?)>(
            svc => svc.BlokirajAsync(klijentId, majstorId));

        uspeh.Should().BeTrue();
        greska.Should().BeNull();

        (await Query(db => db.UserBlocks.CountAsync(
            b => b.BlockerId == klijentId && b.BlockedId == majstorId)))
            .Should().Be(1);
    }

    [Fact]
    public async Task Odblokiranje_NeSkidaTudjuBlokadu()
    {
        // Oba su blokirala jedan drugog. Kad jedan odblokira, blokada MORA
        // ostati — inače bi „odblokiraj" bio način da se skine tuđa odluka i
        // povrati pristup osobi koja se sklonila.
        var (klijentId, majstorId, oglasId) = await PostaviAsync();

        await BlokirajAsync(klijentId, majstorId);
        await BlokirajAsync(majstorId, klijentId);

        await WithService<BlockService>(svc => svc.OdblokirajAsync(klijentId, majstorId));

        var rezultat = await PretraziAsync(klijentId);
        rezultat.Items.Should().NotContain(l => l.Id == oglasId,
            "majstorova blokada i dalje važi");
    }

    [Fact]
    public async Task ListaBlokiranih_PrikazujeSamoSopstveneBlokade()
    {
        // Ko je mene blokirao ne sme da se vidi u mojoj listi: ne mogu to da
        // uklonim, a i samo saznanje pretvara blokadu u obaveštenje.
        var (klijentId, majstorId, _) = await PostaviAsync();

        await BlokirajAsync(majstorId, klijentId);

        var lista = await WithService<BlockService, List<UsluzionicaServer.DTOs.Moderation.BlockedUserDto>>(
            svc => svc.ListaBlokiranihAsync(klijentId));

        lista.Should().BeEmpty();
    }
}
