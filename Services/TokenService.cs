using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UsluzionicaServer.Domain.Entities;

namespace UsluzionicaServer.Services;

/// <summary>
/// Generiše JWT access token i kriptografski sigurni refresh token.
/// </summary>
public sealed class TokenService(IConfiguration config)
{
    // ── JWT Access Token ───────────────────────────────────────────────────
    public string GenerateAccessToken(ApplicationUser user, IList<string> roles)
    {
        var jwtSection = config.GetSection("Jwt");
        var key        = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Secret"]!));
        var creds      = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires    = DateTime.UtcNow.AddMinutes(
            int.Parse(jwtSection["AccessTokenExpirationMinutes"] ?? "60"));

        // Claims = podaci "ugravirani" u token (server ih ne mora čuvati)
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email,          user.Email!),
            new("fullName",                user.FullName),
            new("isProvider",              user.IsProvider.ToString().ToLower()),
        };

        // Dodajemo role kao zasebne claims (može biti više: User + Admin)
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var token = new JwtSecurityToken(
            issuer:             jwtSection["Issuer"],
            audience:           jwtSection["Audience"],
            claims:             claims,
            expires:            expires,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── Refresh Token ──────────────────────────────────────────────────────
    // Nije JWT — samo nasumičan niz bajtova pretvorenih u Base64 string.
    // Čuvamo ga u bazi i koristimo da produžimo sesiju bez ponovnog logina.
    public static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    // ── Generiši jedinstveni referral kod ─────────────────────────────────
    // 8 karaktera, samo velika slova i cifre (bez 0/O/I/L zbunjujućih znakova)
    public static string GenerateReferralCode()
    {
        const string chars  = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var          result = new char[8];
        var          bytes  = RandomNumberGenerator.GetBytes(8);
        for (int i = 0; i < 8; i++)
            result[i] = chars[bytes[i] % chars.Length];
        return new string(result);
    }
}
