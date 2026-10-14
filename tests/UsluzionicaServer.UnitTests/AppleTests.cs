using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using UsluzionicaServer.Infrastructure.Apple;
using UsluzionicaServer.Infrastructure.Push;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Apple: JWT-ovi potpisani `.p8` ključem (APNs, Sign in with Apple), čitanje
/// Apple-ovog identiteta i odluka koje APNs tokene treba obrisati.
/// </summary>
public class AppleTests
{
    // Nov P-256 ključ u PEM obliku — isti format kao Apple-ov `.p8`.
    private static readonly ECDsa Kljuc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string Pem = Kljuc.ExportPkcs8PrivateKeyPem();

    private static AppleKljuc Napravi(string? privateKey = null) => new(TestConfig.From(new()
    {
        ["Apple:TeamId"]     = "TEAM123456",
        ["Apple:KeyId"]      = "KEY1234567",
        ["Apple:PrivateKey"] = privateKey ?? Convert.ToBase64String(Encoding.UTF8.GetBytes(Pem))
    }));

    private static async Task<TokenValidationResult> Proveri(string jwt) =>
        await new JsonWebTokenHandler().ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            IssuerSigningKey = new ECDsaSecurityKey(Kljuc),
            ValidateIssuer   = false,
            ValidateAudience = false,
            ValidateLifetime = false
        });

    // ── Ključ i JWT ────────────────────────────────────────────────────────

    [Fact]
    public void BezKljuca_NijePodesen()
    {
        var k = new AppleKljuc(TestConfig.From(new() { ["Apple:TeamId"] = "TEAM123456" }));
        k.Podesen.Should().BeFalse();
    }

    [Fact]
    public void KljucSePrimaIKaoPem_IKaoBase64()
    {
        Napravi().Podesen.Should().BeTrue();
        Napravi(Pem).Podesen.Should().BeTrue();
    }

    [Fact]
    public async Task ApnsToken_ES256_SaKidIIss_PotpisJeIspravan()
    {
        var jwt = Napravi().ApnsToken();
        var t   = new JsonWebToken(jwt);

        t.Alg.Should().Be("ES256");
        t.Kid.Should().Be("KEY1234567");
        t.Issuer.Should().Be("TEAM123456");
        t.TryGetPayloadValue<long>("iat", out _).Should().BeTrue();

        // APNs odbija tokene sa viškom vremenskih claim-ova — samo iss i iat.
        t.TryGetPayloadValue<long>("exp", out _).Should().BeFalse();

        (await Proveri(jwt)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ApnsToken_SeKoristiPonovo_NePraviSeZaSvakiPush()
    {
        // Apple odbija prečesto obnavljanje (TooManyProviderTokenUpdates).
        var k = Napravi();
        k.ApnsToken().Should().Be(k.ApnsToken());
    }

    [Fact]
    public async Task ClientSecret_ImaAudSubIExp()
    {
        var jwt = Napravi().ClientSecret();
        var t   = new JsonWebToken(jwt);

        t.Audiences.Should().ContainSingle().Which.Should().Be("https://appleid.apple.com");
        t.Subject.Should().Be("rs.usluzionica.app");
        t.ValidTo.Should().BeAfter(DateTime.UtcNow).And.BeBefore(DateTime.UtcNow.AddMinutes(6));
        (await Proveri(jwt)).IsValid.Should().BeTrue();
    }

    // ── Identitet iz Apple tokena ──────────────────────────────────────────

    [Theory]
    [InlineData(true,   true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void EmailVerified_IKaoBoolIKaoTekst(object vrednost, bool ocekivano)
    {
        var id = AppleSignIn.IzClaimova(new Dictionary<string, object>
        {
            ["sub"] = "000123.abc", ["email"] = "x@privaterelay.appleid.com", ["email_verified"] = vrednost
        });

        id!.Sub.Should().Be("000123.abc");
        id.EmailPotvrdjen.Should().Be(ocekivano);
    }

    [Fact]
    public void BezSub_NemaIdentiteta()
    {
        AppleSignIn.IzClaimova(new Dictionary<string, object> { ["email"] = "a@b.rs" }).Should().BeNull();
    }

    // ── APNs: koje tokene brisati ──────────────────────────────────────────

    private sealed class Odgovori(Func<string, HttpResponseMessage> odgovor) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Zahtevi { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Zahtevi.Add(r);
            var token = r.RequestUri!.Segments[^1];
            return Task.FromResult(odgovor(token));
        }
    }

    private static HttpResponseMessage Greska(HttpStatusCode s, string razlog) =>
        new(s) { Content = new StringContent($"{{\"reason\":\"{razlog}\"}}", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Apns_BriseSeSamoTokenKojiViseNePostoji()
    {
        var handler = new Odgovori(t => t switch
        {
            "ziv"         => new HttpResponseMessage(HttpStatusCode.OK),
            "obrisan"     => Greska(HttpStatusCode.Gone, "Unregistered"),
            "los"         => Greska(HttpStatusCode.BadRequest, "BadDeviceToken"),
            "pogresan-id" => Greska(HttpStatusCode.BadRequest, "DeviceTokenNotForTopic"),
            _             => Greska(HttpStatusCode.Forbidden, "InvalidProviderToken")
        });
        var fabrika = Substitute.For<IHttpClientFactory>();
        fabrika.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var sender = new ApplePushSender(fabrika, Napravi(), TestConfig.From(new()),
                                         NullLogger<ApplePushSender>.Instance);

        var mrtvi = await sender.SendAsync(
            ["ziv", "obrisan", "los", "pogresan-id", "kljuc-problem"],
            new PushMessage(1, "NewMessage", "Naslov", "Telo", 7));

        // Naša greška (topic, ključ) NE briše korisnikov uređaj.
        mrtvi.Should().BeEquivalentTo(["obrisan", "los"]);

        var prvi = handler.Zahtevi[0];
        prvi.RequestUri!.Host.Should().Be("api.push.apple.com");
        prvi.Headers.GetValues("apns-topic").Should().ContainSingle().Which.Should().Be("rs.usluzionica.app");
        prvi.Headers.Authorization!.Scheme.Should().Be("bearer");
    }

    [Fact]
    public void Apns_TeloNosiKindIReferenceId_VanAps()
    {
        var telo = ApplePushSender.Telo(new PushMessage(5, "NewMessage", "N", "T", 42));
        telo["kind"].Should().Be("NewMessage");
        telo["referenceId"].Should().Be("42");
        telo.Should().ContainKey("aps");
    }
}
