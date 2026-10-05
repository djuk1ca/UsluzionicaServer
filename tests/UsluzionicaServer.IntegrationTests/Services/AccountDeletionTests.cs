using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.DTOs.Users;
using UsluzionicaServer.Infrastructure.ExternalAuth;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;
using static UsluzionicaServer.Services.ExternalLoginService;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Brisanje naloga — za naloge SA i BEZ lozinke.
///
/// Google Play i Apple traže da svaki nalog može da se obriše iz aplikacije.
/// Pre ove izmene nalog napravljen preko Google-a/Facebook-a to NIJE mogao:
/// servis je tražio lozinku koju takav nalog nikad nije imao. To je razlog za
/// odbijanje izdanja, ne samo bag.
/// </summary>
public class AccountDeletionTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static ExternalIdentity Google(string email = "google@test.rs") =>
        new("Google", "google-sub-brisanje", email, EmailVerified: true, Name: "Ana Anić");

    private Task<(ApplicationUser? User, string? Error)> RegistrujPrekoGoogleaAsync() =>
        WithService<ExternalLoginService, (ApplicationUser?, string?)>(svc => svc.RegistrujAsync(
            Google(),
            new ExternalRegisterRequest { SignupToken = "x", FullName = "Ana Anić", AcceptedPolicy = true },
            referralIzPrijave: null));

    private Task<(bool Uspeh, string? Greska)> ObrisiAsync(string userId, string? lozinka, string? rec) =>
        WithService<UserService, (bool, string?)>(svc => svc.DeleteAccountAsync(userId, lozinka, rec));

    // ── Nalog bez lozinke ──────────────────────────────────────────────────

    [Fact]
    public async Task BezLozinke_BezPotvrdneReci_Odbijeno()
    {
        var (user, _) = await RegistrujPrekoGoogleaAsync();

        var (uspeh, greska) = await ObrisiAsync(user!.Id, lozinka: null, rec: null);

        uspeh.Should().BeFalse();
        greska.Should().Contain(UserService.PotvrdnaRec);
    }

    [Theory]
    [InlineData("OBRIŠI")]
    [InlineData("obriši")]
    [InlineData(" OBRISI ")]   // bez dijakritike — srpska tastatura nije uvek pri ruci
    public async Task BezLozinke_SaPotvrdnomReci_Obrisan(string rec)
    {
        // NAJVAŽNIJI TEST U KLASI — ovo je pre izmene bilo nemoguće.
        var (user, _) = await RegistrujPrekoGoogleaAsync();

        var (uspeh, greska) = await ObrisiAsync(user!.Id, lozinka: null, rec);

        uspeh.Should().BeTrue(greska);
        var obrisan = await Query(db => db.Users.SingleAsync(u => u.Id == user.Id));
        obrisan.IsActive.Should().BeFalse();
        obrisan.Email.Should().NotBe("google@test.rs");
    }

    [Fact]
    public async Task PosleBrisanja_IstiGoogleNalog_MozeIspocetka()
    {
        // Veza sa Google-om se raskida. Bez toga bi ponovna prijava istim
        // Google nalogom našla obrisan, deaktiviran nalog i odbila je — osoba
        // ne bi mogla ni da napravi nov nalog.
        var (user, _) = await RegistrujPrekoGoogleaAsync();
        await ObrisiAsync(user!.Id, null, "OBRIŠI");

        var ishod = await WithService<ExternalLoginService, Ishod>(svc => svc.RazresiAsync(Google()));
        ishod.Should().BeOfType<TrebaRegistracija>();

        var (nov, greska) = await RegistrujPrekoGoogleaAsync();
        greska.Should().BeNull();
        nov!.Id.Should().NotBe(user.Id);
    }

    // ── Nalog sa lozinkom ──────────────────────────────────────────────────

    [Fact]
    public async Task SaLozinkom_PotvrdnaRecNeZamenjujeLozinku()
    {
        // Inače bi ukraden JWT (otključan telefon u tuđim rukama) bio dovoljan
        // da se obriše nalog koji ima lozinku.
        var user = await Data.CreateConfirmedUserAsync("lozinka@test.rs");

        var (uspeh, _) = await ObrisiAsync(user.Id, lozinka: null, rec: "OBRIŠI");

        uspeh.Should().BeFalse();
        (await Query(db => db.Users.SingleAsync(u => u.Id == user.Id))).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SaLozinkom_IspravnaLozinka_Obrisan()
    {
        var user = await Data.CreateConfirmedUserAsync("lozinka@test.rs");

        var (uspeh, greska) = await ObrisiAsync(user.Id, TestData.DefaultPassword, rec: null);

        uspeh.Should().BeTrue(greska);
    }

    // ── Ostalo ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Profil_KazeDaLiNalogImaLozinku()
    {
        // Po ovome aplikacija bira da li traži lozinku ili potvrdnu reč.
        var (google, _) = await RegistrujPrekoGoogleaAsync();
        var lozinka     = await Data.CreateConfirmedUserAsync("lozinka@test.rs");

        var p1 = await WithService<UserService, UserProfileDto?>(svc => svc.GetProfileAsync(google!.Id));
        var p2 = await WithService<UserService, UserProfileDto?>(svc => svc.GetProfileAsync(lozinka.Id));

        p1!.HasPassword.Should().BeFalse();
        p2!.HasPassword.Should().BeTrue();
    }

    [Fact]
    public async Task Brisanje_UklanjaUredjajeZaObavestenja()
    {
        var (user, _) = await RegistrujPrekoGoogleaAsync();
        await Query(async db =>
        {
            db.DeviceTokens.Add(new DeviceToken { UserId = user!.Id, Token = "fcm-token-brisanje-1", Platform = "android" });
            return await db.SaveChangesAsync();
        });

        await ObrisiAsync(user!.Id, null, "OBRIŠI");

        (await Query(db => db.DeviceTokens.CountAsync(t => t.UserId == user.Id))).Should().Be(0);
    }
}
