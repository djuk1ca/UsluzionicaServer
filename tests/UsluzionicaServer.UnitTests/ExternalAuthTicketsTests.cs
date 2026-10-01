using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using UsluzionicaServer.Infrastructure.ExternalAuth;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti dve brane prijave preko Google-a i Facebook-a:
///
///   • PKCE — kod iz povratne adrese vredi SAMO uz tajnu aplikacije koja je
///     prijavu započela. Bez toga bi tuđa aplikacija sa istom šemom
///     (`usluzionica://`) uhvatila kod i dobila naše tokene.
///   • Svrha tokena — `state` ne sme da se podmetne kao kod rezultata. Isti
///     mehanizam potpisuje oba, pa jedino ime svrhe drži ta dva sveta odvojeno.
/// </summary>
public class ExternalAuthTicketsTests
{
    private static ExternalAuthTickets CreateSut() =>
        new(new EphemeralDataProtectionProvider());

    private static (string Verifier, string Challenge) NovPar()
    {
        var verifier  = ExternalAuthTickets.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = ExternalAuthTickets.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    [Fact]
    public void Pkce_IspravnaTajna_Prolazi()
    {
        var (verifier, challenge) = NovPar();

        ExternalAuthTickets.VerifierMatches(verifier, challenge).Should().BeTrue();
    }

    [Fact]
    public void Pkce_TudjaTajna_Odbijena()
    {
        // Presretač ima kod i challenge (oba su putovala kroz adresu), ali ne i
        // originalni verifier — a sa bilo kojim drugim razmena mora da padne.
        var (_, challenge)   = NovPar();
        var (tudjVerifier, _) = NovPar();

        ExternalAuthTickets.VerifierMatches(tudjVerifier, challenge).Should().BeFalse();
    }

    [Fact]
    public void Pkce_ChallengeKaoVerifier_Odbijen()
    {
        // Napadač koji vidi challenge ne sme da ga pošalje kao verifier.
        var (_, challenge) = NovPar();

        ExternalAuthTickets.VerifierMatches(challenge, challenge).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("prekratko")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+")]   // 43 znaka, ali '+' nije base64url
    public void Challenge_Neispravan_Odbijen(string? challenge) =>
        ExternalAuthTickets.IsValidChallenge(challenge).Should().BeFalse();

    [Fact]
    public void Rezultat_PrezivljavaPutNazad()
    {
        var sut = CreateSut();
        var identitet = new ExternalIdentity("Google", "sub-1", "a@b.rs", true, "Ana");

        var token = sut.ProtectResult(new("challenge", null, identitet, "ABCD1234"));
        var nazad = sut.UnprotectResult(token);

        nazad.Should().NotBeNull();
        nazad!.Identity.Should().Be(identitet);
        nazad.ReferralCode.Should().Be("ABCD1234");
    }

    [Fact]
    public void State_NeMozeDaSePodmetneKaoRezultat()
    {
        // Oba se potpisuju istim ključevima. Da svrha nije različita, `state`
        // koji je javno prošao kroz Google-ovu adresu mogao bi da se pošalje na
        // razmenu kao da je kod rezultata.
        var sut   = CreateSut();
        var state = sut.ProtectState(new("google", "challenge", null));

        sut.UnprotectResult(state).Should().BeNull();
        sut.UnprotectSignup(state).Should().BeNull();
    }

    [Fact]
    public void IzmenjenToken_Odbijen()
    {
        var sut   = CreateSut();
        var token = sut.ProtectSignup(new(new ExternalIdentity("Facebook", "1", "a@b.rs", false, "A"), null));

        // Jedan promenjen znak u sredini.
        var sredina  = token.Length / 2;
        var izmenjen = token[..sredina] + (token[sredina] == 'A' ? 'B' : 'A') + token[(sredina + 1)..];

        sut.UnprotectSignup(izmenjen).Should().BeNull();
    }

    [Fact]
    public void Smece_NeBacaIzuzetak()
    {
        // Kod iz adrese je nepoverljiv ulaz. Izuzetak bi ovde bio 500 na
        // javnom endpointu — umesto obične poruke „pokušaj ponovo".
        CreateSut().UnprotectResult("nije-uopste-token").Should().BeNull();
    }
}
