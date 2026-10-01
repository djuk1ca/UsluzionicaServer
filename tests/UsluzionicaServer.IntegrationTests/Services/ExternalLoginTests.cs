using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Infrastructure.ExternalAuth;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;
using static UsluzionicaServer.Services.ExternalLoginService;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti odluke koje prijava preko Google-a i Facebook-a donosi o NALOGU.
///
/// Sam razgovor sa provajderom se ovde ne testira — on traži pravi Google.
/// Testira se ono što dolazi posle: identitet je stigao, šta sad sa njim. Tu
/// žive svi opasni slučajevi — duplirani nalozi, preuzimanje tuđeg naloga,
/// nalog bez saglasnosti — i svi su nezavisni od mreže.
/// </summary>
public class ExternalLoginTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static ExternalIdentity Google(string email, string sub = "google-sub-1") =>
        new("Google", sub, email, EmailVerified: true, Name: "Ana Anić");

    private static ExternalIdentity Facebook(string email, string id = "fb-id-1") =>
        new("Facebook", id, email, EmailVerified: false, Name: "Ana Anić");

    private Task<Ishod> RazresiAsync(ExternalIdentity id) =>
        WithService<ExternalLoginService, Ishod>(svc => svc.RazresiAsync(id));

    private Task<(ApplicationUser? User, string? Error)> RegistrujAsync(
        ExternalIdentity id, bool prihvatio = true, string? referral = null) =>
        WithService<ExternalLoginService, (ApplicationUser?, string?)>(svc => svc.RegistrujAsync(
            id,
            new ExternalRegisterRequest
            {
                SignupToken    = "nebitno-ovde",
                FullName       = "Ana Anić",
                City           = TestData.ValidCity,
                ReferralCode   = referral,
                AcceptedPolicy = prihvatio
            },
            referralIzPrijave: null));

    // ── Nov korisnik ───────────────────────────────────────────────────────

    [Fact]
    public async Task NovIdentitet_TraziRegistraciju_INePraviNalog()
    {
        // Nalog se NE pravi pri povratku sa Google-a. Korisnik koji odustane na
        // „Dovrši nalog" ne sme ostaviti nalog bez saglasnosti sa politikom.
        var ishod = await RazresiAsync(Google("nova@test.rs"));

        ishod.Should().BeOfType<TrebaRegistracija>();
        (await Query(db => db.Users.CountAsync(u => u.Email == "nova@test.rs"))).Should().Be(0);
    }

    [Fact]
    public async Task Registracija_PraviPotvrdjenNalogSaSaglasnoscuIVezom()
    {
        var (user, greska) = await RegistrujAsync(Google("nova@test.rs"));

        greska.Should().BeNull();
        user.Should().NotBeNull();
        user!.EmailConfirmed.Should().BeTrue("mejl je potvrdio Google");
        user.PolicyAcceptedAt.Should().NotBeNull();
        user.LastKnownCity.Should().Be(TestData.ValidCity);

        // Sledeća prijava istim Google nalogom mora naći baš ovaj nalog.
        var ishod = await RazresiAsync(Google("nova@test.rs"));
        ishod.Should().BeOfType<Prijavljen>().Which.User.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task Registracija_NemaLozinku()
    {
        // Nasumična lozinka koju niko ne zna bila bi samo još jedna meta.
        var (user, _) = await RegistrujAsync(Google("nova@test.rs"));

        var imaLozinku = await WithService<UserManager<ApplicationUser>, bool>(
            um => um.HasPasswordAsync(user!));

        imaLozinku.Should().BeFalse();
    }

    [Fact]
    public async Task Registracija_BezSaglasnosti_Odbijena()
    {
        var (user, greska) = await RegistrujAsync(Google("nova@test.rs"), prihvatio: false);

        user.Should().BeNull();
        greska.Should().NotBeNull();
        (await Query(db => db.Users.CountAsync(u => u.Email == "nova@test.rs"))).Should().Be(0);
    }

    [Fact]
    public async Task DuplaRegistracija_IstiNalog_NeDrugi()
    {
        // Karta za registraciju važi 30 minuta; dupli tap ne sme napraviti dva naloga.
        var (prvi, _)  = await RegistrujAsync(Google("nova@test.rs"));
        var (drugi, g) = await RegistrujAsync(Google("nova@test.rs"));

        g.Should().BeNull();
        drugi!.Id.Should().Be(prvi!.Id);
        (await Query(db => db.Users.CountAsync(u => u.Email == "nova@test.rs"))).Should().Be(1);
    }

    [Fact]
    public async Task Registracija_SaReferralom_IsplacujePrvuRatuOdmah()
    {
        // Kod registracije lozinkom rata ide pri kliku na verifikacioni link.
        // Ovde je mejl već potvrđen — da čeka link, rata ne bi stigla nikad.
        var pozivalac = await Data.CreateConfirmedUserAsync("pozivalac@test.rs");

        var (user, _) = await RegistrujAsync(Google("nova@test.rs"), referral: pozivalac.ReferralCode);

        var referral = await Query(db => db.Referrals.SingleAsync(r => r.ReferredUserId == user!.Id));
        referral.Status.Should().NotBe(Domain.Enums.ReferralStatus.Pending);
    }

    // ── Postojeći nalog ────────────────────────────────────────────────────

    [Fact]
    public async Task Google_PostojeciPotvrdjenNalog_SePovezuje()
    {
        var postojeci = await Data.CreateConfirmedUserAsync("ana@test.rs");

        var ishod = await RazresiAsync(Google("ana@test.rs"));

        ishod.Should().BeOfType<Prijavljen>().Which.User.Id.Should().Be(postojeci.Id);
        (await Query(db => db.Users.CountAsync(u => u.Email == "ana@test.rs"))).Should().Be(1,
            "isti čovek ne sme dobiti dva naloga");
    }

    [Fact]
    public async Task Google_NepotvrdjenNalog_LozinkaSeUklanja()
    {
        // PREUZIMANJE UNAPRED: napadač registruje tuđ mejl lozinkom i nikad ga
        // ne potvrdi. Kad se pravi vlasnik prijavi Google-om, nalog se poveže i
        // mejl postane potvrđen — pa bi napadačeva lozinka od tada radila.
        var napadacev = await Data.CreateUnconfirmedUserAsync("zrtva@test.rs");

        await RazresiAsync(Google("zrtva@test.rs"));

        var (potvrdjen, imaLozinku) = await WithService<UserManager<ApplicationUser>, (bool, bool)>(async um =>
        {
            var u = await um.FindByIdAsync(napadacev.Id);
            return (u!.EmailConfirmed, await um.HasPasswordAsync(u));
        });

        potvrdjen.Should().BeTrue();
        imaLozinku.Should().BeFalse("lozinka nepotvrđenog naloga pripada nekome ko mejl nije dokazao");
    }

    [Fact]
    public async Task Facebook_PostojeciNalog_NePovezujeSeAutomatski()
    {
        // Facebook ne garantuje da je mejl potvrđen. Povezivanje po mejlu bi
        // svakome ko napravi Facebook nalog sa tuđim mejlom predalo tuđ nalog.
        await Data.CreateConfirmedUserAsync("ana@test.rs");

        var ishod = await RazresiAsync(Facebook("ana@test.rs"));

        ishod.Should().BeOfType<Odbijen>().Which.Greska.Should().Be(Greske.NalogPostoji);
        (await Query(db => db.UserLogins.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Facebook_NovKorisnik_MozeDaSeRegistruje()
    {
        // Pozitivna kontrola za test iznad: ograničenje važi samo za POSTOJEĆE naloge.
        (await RazresiAsync(Facebook("nova@test.rs"))).Should().BeOfType<TrebaRegistracija>();

        var (user, greska) = await RegistrujAsync(Facebook("nova@test.rs"));

        greska.Should().BeNull();
        (await RazresiAsync(Facebook("nova@test.rs")))
            .Should().BeOfType<Prijavljen>().Which.User.Id.Should().Be(user!.Id);
    }

    [Fact]
    public async Task PromenjenMejlKodProvajdera_IstiNalog()
    {
        // Ključ je id kod provajdera, ne mejl. Korisnik koji promeni mejl kod
        // Google-a mora ostati u svom nalogu, a ne dobiti nov prazan.
        var (user, _) = await RegistrujAsync(Google("stara@test.rs", sub: "isti-sub"));

        var ishod = await RazresiAsync(Google("nova-adresa@test.rs", sub: "isti-sub"));

        ishod.Should().BeOfType<Prijavljen>().Which.User.Id.Should().Be(user!.Id);
    }

    [Fact]
    public async Task BezMejla_Odbijen()
    {
        var ishod = await RazresiAsync(new ExternalIdentity("Facebook", "fb-bez-mejla", null, false, "Ana"));

        ishod.Should().BeOfType<Odbijen>().Which.Greska.Should().Be(Greske.NemaEmail);
    }

    [Fact]
    public async Task DeaktiviranNalog_Odbijen()
    {
        var (user, _) = await RegistrujAsync(Google("nova@test.rs"));

        await WithService<UserManager<ApplicationUser>>(async um =>
        {
            var u = await um.FindByIdAsync(user!.Id);
            u!.IsActive = false;
            await um.UpdateAsync(u);
        });

        var ishod = await RazresiAsync(Google("nova@test.rs"));

        ishod.Should().BeOfType<Odbijen>().Which.Greska.Should().Be(Greske.Deaktiviran);
    }
}
