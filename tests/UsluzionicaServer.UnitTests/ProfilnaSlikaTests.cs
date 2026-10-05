using UsluzionicaServer.Infrastructure.ExternalAuth;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti listu domena sa kojih server sme da preuzme profilnu sliku.
///
/// Server ovde otvara adresu koju nije sam napisao. Propust u ovoj proveri je
/// SSRF: zahtev iz našeg kontejnera ka adresi po izboru napadača — uključujući
/// bazu i Redis na istoj Docker mreži.
/// </summary>
public class ProfilnaSlikaTests
{
    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/ACg8ocK=s400-c")]
    [InlineData("https://platform-lookaside.fbsbx.com/platform/profilepic/?asid=1")]
    [InlineData("https://scontent-vie1-1.xx.fbcdn.net/v/t1.30497-1/abc.jpg")]
    public void ProvajderoviCdn_Dozvoljeni(string url) =>
        ProfilnaSlikaProvajdera.JeDozvoljena(new Uri(url)).Should().BeTrue();

    [Theory]
    [InlineData("http://lh3.googleusercontent.com/a/x")]             // bez TLS-a
    [InlineData("https://evilgoogleusercontent.com/a/x")]            // sličan, a tuđ domen
    [InlineData("https://googleusercontent.com.napadac.rs/a/x")]     // naš domen kao poddomen tuđeg
    [InlineData("https://lh3.googleusercontent.com:8443/a/x")]       // nestandardan port
    [InlineData("https://db/uploads")]                               // ime servisa na Docker mreži
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]        // metapodaci cloud servera
    public void SveOstalo_Odbijeno(string url) =>
        ProfilnaSlikaProvajdera.JeDozvoljena(new Uri(url)).Should().BeFalse();

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/abc=s96-c", "https://lh3.googleusercontent.com/a/abc=s400-c")]
    [InlineData("https://lh3.googleusercontent.com/a/abc",       "https://lh3.googleusercontent.com/a/abc")]
    [InlineData(null,                                            null)]
    public void GoogleSlika_TraziSeVecaVerzija(string? ulaz, string? ocekivano) =>
        GoogleAuthProvider.VecaSlika(ulaz).Should().Be(ocekivano);
}
