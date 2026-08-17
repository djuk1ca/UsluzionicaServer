using Microsoft.Extensions.Configuration;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Pravi pravi <see cref="IConfiguration"/> iz rečnika.
///
/// Zašto ne NSubstitute mock: <c>IConfiguration</c> se čita indekserom
/// (<c>config["Jwt:Secret"]</c>) i preko <c>GetSection</c>. Mock-ovanje toga
/// znači ručno namestiti svaki poziv, uključujući ugnježdene sekcije — puno
/// koda koji testira mock umesto pravog ponašanja.
///
/// <c>AddInMemoryCollection</c> je pravi konfiguracioni provajder: razume
/// dvotačku kao razdvajač sekcija i ponaša se tačno kao produkcijski.
/// </summary>
internal static class TestConfig
{
    /// <summary>Ključ koji dekodira u tačno 32 bajta — AES-256 zahteva toliko.</summary>
    public const string ValidAesKeyBase64 = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    /// <summary>Dovoljno dug da prođe SecretsGuard (min 32 znaka) i nije na listi kompromitovanih.</summary>
    public const string ValidJwtSecret = "test-jwt-secret-koji-je-dovoljno-dug-za-hmac-sha256-2026";

    public static IConfiguration From(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    /// <summary>Minimalna ispravna konfiguracija; pojedini ključevi se prepisuju po testu.</summary>
    public static Dictionary<string, string?> ValidBase() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=Test;",
        ["Jwt:Secret"]                          = ValidJwtSecret,
        ["Jwt:Issuer"]                          = "usluzionica-api",
        ["Jwt:Audience"]                        = "usluzionica-app",
        ["Encryption:MessageKey"]               = ValidAesKeyBase64,
        ["AdminSeed:Password"]                  = "AdminTest123!",
    };
}
