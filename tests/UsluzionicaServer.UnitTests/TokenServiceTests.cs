using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: JWT mora nositi tačno one podatke na koje se oslanjaju
/// autorizacija na serveru i AppState na klijentu. Ako claim nestane ili
/// promeni ime, tiho pada autorizacija — a to se ne vidi dok neko ne ostane
/// zaključan van admin panela.
/// </summary>
public class TokenServiceTests
{
    private static TokenService CreateSut() =>
        new(TestConfig.From(TestConfig.ValidBase()));

    private static ApplicationUser SampleUser() => new()
    {
        Id         = "user-123",
        Email      = "test@usluzionica.rs",
        FullName   = "Petar Petrović",
        IsProvider = true
    };

    private static JwtSecurityToken Decode(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt);

    [Fact]
    public void GenerateAccessToken_NosiIdentifikatorKorisnika()
    {
        var token = Decode(CreateSut().GenerateAccessToken(SampleUser(), []));

        // Ceo server čita korisnika iz ovog claim-a (ClaimTypes.NameIdentifier).
        token.Claims.Should().Contain(c =>
            c.Type == ClaimTypes.NameIdentifier && c.Value == "user-123");
    }

    [Fact]
    public void GenerateAccessToken_NosiRoleKaoZasebneClaimove()
    {
        var token = Decode(CreateSut().GenerateAccessToken(SampleUser(), ["User", "Admin"]));

        // [Authorize(Roles = "Admin")] zavisi tačno od ovoga.
        var roles = token.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value);
        roles.Should().BeEquivalentTo("User", "Admin");
    }

    [Fact]
    public void GenerateAccessToken_KadaKorisnikNijeProvajder_IsProviderJeFalse()
    {
        var user = SampleUser();
        user.IsProvider = false;

        var token = Decode(CreateSut().GenerateAccessToken(user, []));

        token.Claims.Should().Contain(c => c.Type == "isProvider" && c.Value == "false");
    }

    [Fact]
    public void GenerateAccessToken_PostavljaIssuerIAudience()
    {
        // ValidateIssuer/ValidateAudience su uključeni u Program.cs — ako se
        // ovde razmimoiđu, svaki token biva odbijen sa 401.
        var token = Decode(CreateSut().GenerateAccessToken(SampleUser(), []));

        token.Issuer.Should().Be("usluzionica-api");
        token.Audiences.Should().Contain("usluzionica-app");
    }

    [Fact]
    public void GenerateReferralCode_NemaZbunjujucihZnakova()
    {
        // Kod se prepisuje ručno (deli se sa prijateljem), pa 0/O i 1/I/L
        // moraju biti izbačeni da se ne meša pri kucanju.
        var codes = Enumerable.Range(0, 200)
            .Select(_ => TokenService.GenerateReferralCode())
            .ToList();

        codes.Should().OnlyContain(c => c.Length == 8);
        codes.SelectMany(c => c).Should().OnlyContain(ch => "ABCDEFGHJKMNPQRSTUVWXYZ23456789".Contains(ch));
    }

    [Fact]
    public void GenerateRefreshToken_JeDovoljnoDugIRazlicitSvakiPut()
    {
        var tokens = Enumerable.Range(0, 50)
            .Select(_ => TokenService.GenerateRefreshToken())
            .ToList();

        // 64 nasumična bajta → Base64. Kolizija bi značila da neko može pogoditi
        // tuđi refresh token.
        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(t => Convert.FromBase64String(t).Length == 64);
    }
}
