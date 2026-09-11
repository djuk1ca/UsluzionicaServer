using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Štiti pravilo: nalog ne može nastati bez saglasnosti sa politikom
/// privatnosti, i ta saglasnost mora ostaviti trag.
///
/// Zašto je vredno testa: klijent već onemogućava dugme dok box nije čekiran, pa
/// se lako pomisli da je to dovoljno. Nije — HTTP zahtev se može poslati i mimo
/// aplikacije, a ZZPL (i GDPR čl. 7) traže da rukovalac MOŽE DA DOKAŽE
/// saglasnost. Čekiran box koji nigde ne ostavlja zapis ne dokazuje ništa.
/// </summary>
public class PolicyConsentTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static RegisterRequest Zahtev(bool prihvata) => new()
    {
        FullName       = "Test Korisnik",
        Email          = "policy@test.rs",
        Password       = TestData.DefaultPassword,
        AcceptedPolicy = prihvata
    };

    [Fact]
    public async Task Registracija_BezSaglasnosti_Odbijena()
    {
        // NEGATIVAN TEST — bez njega bi kod koji uopšte ne proverava saglasnost
        // prošao test ispod. Ovo je jedini test koji dokazuje da pravilo postoji.

        // Act
        var (uspeh, greske) = await WithService<AuthService, (bool, string[])>(
            svc => svc.RegisterAsync(Zahtev(prihvata: false)));

        // Assert
        uspeh.Should().BeFalse();
        greske.Should().ContainSingle()
              .Which.Should().Contain("politiku privatnosti");

        // Neuspeh mora biti POTPUN — nalog ne sme ostati napola napravljen.
        (await Query(db => db.Users.CountAsync(u => u.Email == "policy@test.rs")))
            .Should().Be(0);
    }

    [Fact]
    public async Task Registracija_SaSaglasnoscu_ProlaziIBeleziTrag()
    {
        // POZITIVAN par. Bez njega bi i kod koji odbija SVAKU registraciju
        // prošao test iznad.

        // Act
        var (uspeh, greske) = await WithService<AuthService, (bool, string[])>(
            svc => svc.RegisterAsync(Zahtev(prihvata: true)));

        // Assert
        uspeh.Should().BeTrue(string.Join(", ", greske));

        var korisnik = await Query(db => db.Users.SingleAsync(u => u.Email == "policy@test.rs"));

        // Sam datum nije dovoljan: bez verzije se posle izmene politike ne može
        // utvrditi ko je prihvatio koju, pa bi se nova saglasnost morala tražiti
        // od SVIH korisnika umesto samo od onih na staroj verziji.
        korisnik.PolicyAcceptedAt.Should().NotBeNull();
        korisnik.PolicyAcceptedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        korisnik.PolicyVersionAccepted.Should().Be(PolicyVersion.Current);
    }

    [Fact]
    public async Task Registracija_NeUpisujeSaglasnostKadJeOdbijena()
    {
        // Granični slučaj koji hvata delimičan upis: ako bi se korisnik pravio
        // pre provere saglasnosti, ostao bi zapis sa datumom a bez naloga —
        // ili gore, nalog bez saglasnosti.

        // Arrange — prvo neuspešan pokušaj
        await WithService<AuthService, (bool, string[])>(
            svc => svc.RegisterAsync(Zahtev(prihvata: false)));

        // Act — pa uspešan, sa istim emailom
        var (uspeh, _) = await WithService<AuthService, (bool, string[])>(
            svc => svc.RegisterAsync(Zahtev(prihvata: true)));

        // Assert — prvi pokušaj nije zauzeo email
        uspeh.Should().BeTrue("odbijena registracija ne sme rezervisati email");

        (await Query(db => db.Users.CountAsync(u => u.Email == "policy@test.rs")))
            .Should().Be(1);
    }
}
