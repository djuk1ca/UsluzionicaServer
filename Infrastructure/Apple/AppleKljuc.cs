using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace UsluzionicaServer.Infrastructure.Apple;

/// <summary>
/// Apple-ov `.p8` ključ i JWT-ovi koji se njim potpisuju.
///
/// JEDAN KLJUČ, DVE NAMENE (Obsidian „iOS / 01" §2): isti ključ ima uključen i
/// APNs i Sign in with Apple. Server njime potpisuje:
///   • bearer token za APNs (push na iPhone)       → <see cref="ApnsToken"/>
///   • „client secret" za Apple-ove /auth/token i /auth/revoke (prijava,
///     poništavanje pri brisanju naloga)          → <see cref="ClientSecret"/>
///
/// Oba su ES256 JWT-ovi sa `kid` = Key ID i `iss` = Team ID; razlikuju se samo
/// po sadržaju. Ključ se čuva u .env kao base64 celog `.p8` fajla (APPLE_PRIVATE_KEY)
/// — jedan red, bez prelamanja koje bi compose pokvario.
///
/// Dok nijedna od tri vrednosti nije podešena, <see cref="Podesen"/> je false i
/// i push i prijava se ponašaju kao isključeni — kao Firebase bez ključa.
/// </summary>
public sealed class AppleKljuc
{
    private readonly ECDsa? _ecdsa;
    private readonly object _brava = new();
    private (string Token, DateTime Napravljen)? _apns;

    public AppleKljuc(IConfiguration config)
    {
        TeamId   = (config["Apple:TeamId"] ?? "").Trim();
        KeyId    = (config["Apple:KeyId"] ?? "").Trim();
        BundleId = config["Apple:BundleId"] is { Length: > 0 } b ? b.Trim() : "rs.usluzionica.app";

        var kljuc = (config["Apple:PrivateKey"] ?? "").Trim();
        if (TeamId.Length > 0 && KeyId.Length > 0 && kljuc.Length > 0)
            _ecdsa = UcitajKljuc(kljuc);
    }

    public string TeamId   { get; }
    public string KeyId    { get; }
    public string BundleId { get; }

    public bool Podesen => _ecdsa is not null;

    /// <summary>
    /// Bearer za APNs. Apple traži da token ne bude stariji od sat vremena, a
    /// odbija i prečesto obnavljanje (TooManyProviderTokenUpdates) — zato se
    /// jedan token koristi 50 minuta za sve zahteve.
    /// </summary>
    public string ApnsToken()
    {
        lock (_brava)
        {
            if (_apns is { } t && DateTime.UtcNow - t.Napravljen < TimeSpan.FromMinutes(50))
                return t.Token;

            var sada = DateTime.UtcNow;
            var token = Potpisi(new Dictionary<string, object>
            {
                ["iss"] = TeamId,
                ["iat"] = EpochTime.GetIntDate(sada)
            });
            _apns = (token, sada);
            return token;
        }
    }

    /// <summary>
    /// „Client secret" za Sign in with Apple REST API. Važi kratko (5 min) —
    /// pravi se za svaki poziv, ne čuva se.
    /// </summary>
    public string ClientSecret()
    {
        var sada = DateTime.UtcNow;
        return Potpisi(new Dictionary<string, object>
        {
            ["iss"] = TeamId,
            ["iat"] = EpochTime.GetIntDate(sada),
            ["exp"] = EpochTime.GetIntDate(sada.AddMinutes(5)),
            ["aud"] = "https://appleid.apple.com",
            ["sub"] = BundleId
        });
    }

    private string Potpisi(Dictionary<string, object> claims)
    {
        if (_ecdsa is null) throw new InvalidOperationException("Apple ključ nije podešen.");

        var opis = new SecurityTokenDescriptor
        {
            Claims             = claims,
            SigningCredentials = new SigningCredentials(
                new ECDsaSecurityKey(_ecdsa) { KeyId = KeyId }, SecurityAlgorithms.EcdsaSha256)
        };

        // Bez podrazumevanih exp/nbf/iat — APNs očekuje samo iss i iat.
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(opis);
    }

    /// <summary>
    /// Prima ili sadržaj `.p8` fajla (PEM, „-----BEGIN PRIVATE KEY-----"),
    /// ili base64 celog fajla — u .env stoji ovo drugo, jer PEM ima prelome redova.
    /// </summary>
    public static ECDsa UcitajKljuc(string vrednost)
    {
        var pem = vrednost.Contains("BEGIN", StringComparison.Ordinal)
            ? vrednost
            : Encoding.UTF8.GetString(Convert.FromBase64String(vrednost));

        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(pem);
        return ecdsa;
    }
}
