using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using UsluzionicaServer.Domain.Entities;

namespace UsluzionicaServer.Infrastructure.Apple;

/// <summary>Ono što Apple garantuje o korisniku posle provere tokena.</summary>
/// <param name="Sub">Stalni Apple id korisnika za NAŠU aplikaciju.</param>
/// <param name="Email">Mejl ili Apple relej (…@privaterelay.appleid.com); može fali.</param>
public sealed record AppleIdentitet(string Sub, string? Email, bool EmailPotvrdjen);

/// <summary>
/// Sign in with Apple — izvorni tok sa iPhone-a.
///
/// Za razliku od Google-a, nema preusmeravanja kroz server: iOS sam prikaže
/// Apple-ov prozor i aplikaciji vrati `identityToken` (JWT koji je Apple
/// potpisao) i jednokratni `authorizationCode`. Server:
///   1. proverava token Apple-ovim javnim ključevima (`aud` = naš Bundle ID),
///   2. kod menja za refresh token — jedini način da se pri brisanju naloga
///      prijava poništi kod Apple-a, što pravilo 5.1.1(v) izričito traži.
/// </summary>
public interface IAppleSignIn
{
    bool Podesen { get; }
    Task<AppleIdentitet?> ProveriAsync(string identityToken, CancellationToken ct = default);
    Task<string?> RefreshTokenAsync(string authorizationCode, CancellationToken ct = default);
    Task<bool> OpozoviAsync(string refreshToken, CancellationToken ct = default);
}

public sealed class AppleSignIn : IAppleSignIn
{
    public const string HttpKlijent = "apple-id";
    private const string Izdavac = "https://appleid.apple.com";

    private readonly AppleKljuc _kljuc;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<AppleSignIn> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _konfiguracija;

    public AppleSignIn(AppleKljuc kljuc, IHttpClientFactory http, ILogger<AppleSignIn> logger)
    {
        _kljuc  = kljuc;
        _http   = http;
        _logger = logger;

        // Apple-ovi javni ključevi se menjaju; ConfigurationManager ih kešira i
        // sam osvežava (i odmah, kad naiđe na nepoznat `kid`).
        _konfiguracija = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{Izdavac}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }

    public bool Podesen => _kljuc.Podesen;

    public async Task<AppleIdentitet?> ProveriAsync(string identityToken, CancellationToken ct = default)
    {
        if (!Podesen || string.IsNullOrWhiteSpace(identityToken)) return null;

        var konf = await _konfiguracija.GetConfigurationAsync(ct);
        var rezultat = await new JsonWebTokenHandler().ValidateTokenAsync(identityToken, new TokenValidationParameters
        {
            ValidIssuer       = Izdavac,
            ValidAudience     = _kljuc.BundleId,
            IssuerSigningKeys = konf.SigningKeys,
            ValidateLifetime  = true,
            ClockSkew         = TimeSpan.FromMinutes(1)
        });

        if (!rezultat.IsValid)
        {
            _logger.LogWarning(rezultat.Exception, "Apple identityToken nije prošao proveru.");
            return null;
        }

        return IzClaimova(rezultat.Claims);
    }

    /// <summary>Iz proverenih claim-ova; izdvojeno da bi se testiralo bez mreže.</summary>
    public static AppleIdentitet? IzClaimova(IDictionary<string, object> c)
    {
        if (!c.TryGetValue("sub", out var sub) || sub?.ToString() is not { Length: > 0 } id) return null;

        var email = c.TryGetValue("email", out var e) ? e?.ToString() : null;

        // Apple šalje email_verified i kao bool i kao string "true" — oba važe.
        var potvrdjen = c.TryGetValue("email_verified", out var v)
                        && string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        return new AppleIdentitet(id, string.IsNullOrWhiteSpace(email) ? null : email, potvrdjen);
    }

    public async Task<string?> RefreshTokenAsync(string authorizationCode, CancellationToken ct = default)
    {
        if (!Podesen || string.IsNullOrWhiteSpace(authorizationCode)) return null;
        try
        {
            using var odgovor = await _http.CreateClient(HttpKlijent).PostAsync($"{Izdavac}/auth/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"]     = _kljuc.BundleId,
                    ["client_secret"] = _kljuc.ClientSecret(),
                    ["code"]          = authorizationCode,
                    ["grant_type"]    = "authorization_code"
                }), ct);

            if (!odgovor.IsSuccessStatusCode)
            {
                _logger.LogWarning("Apple /auth/token: {Status}.", (int)odgovor.StatusCode);
                return null;
            }

            var json = await odgovor.Content.ReadFromJsonAsync<JsonElement>(ct);
            return json.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Apple /auth/token nije dostupan.");
            return null;
        }
    }

    public async Task<bool> OpozoviAsync(string refreshToken, CancellationToken ct = default)
    {
        if (!Podesen || string.IsNullOrWhiteSpace(refreshToken)) return false;
        try
        {
            using var odgovor = await _http.CreateClient(HttpKlijent).PostAsync($"{Izdavac}/auth/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"]       = _kljuc.BundleId,
                    ["client_secret"]   = _kljuc.ClientSecret(),
                    ["token"]           = refreshToken,
                    ["token_type_hint"] = "refresh_token"
                }), ct);

            if (!odgovor.IsSuccessStatusCode)
                _logger.LogWarning("Apple /auth/revoke: {Status}.", (int)odgovor.StatusCode);

            return odgovor.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Apple /auth/revoke nije dostupan.");
            return false;
        }
    }
}

/// <summary>
/// Čuvanje Apple refresh tokena uz nalog i poništavanje pri brisanju naloga.
///
/// Token stoji u Identity tabeli tokena (AspNetUserTokens) — bez nove tabele i
/// migracije — šifrovan Data Protection-om: ko vidi bazu, ne vidi token.
/// </summary>
public sealed class AppleNalog(
    IAppleSignIn                 apple,
    UserManager<ApplicationUser> userManager,
    IDataProtectionProvider      zastita,
    ILogger<AppleNalog>          logger)
{
    public const string Provajder = "apple";
    private const string Ime = "refresh_token";

    private readonly IDataProtector _zastita = zastita.CreateProtector("usluzionica/apple/refresh-token");

    /// <summary>Šifrovan oblik — za kartu „Dovrši nalog", dok nalog još ne postoji.</summary>
    public string Zasticen(string refreshToken) => _zastita.Protect(refreshToken);

    public async Task SacuvajAsync(ApplicationUser user, string? refreshToken, bool vecZasticen = false)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var vrednost = vecZasticen ? refreshToken : _zastita.Protect(refreshToken);
        await userManager.SetAuthenticationTokenAsync(user, Provajder, Ime, vrednost);
    }

    /// <summary>
    /// Poništava Apple prijavu korisnika (ako je ima) i briše sačuvan token.
    /// Ne baca — brisanje naloga ne sme da padne zato što Apple ne odgovara.
    /// </summary>
    public async Task OpozoviAsync(ApplicationUser user)
    {
        try
        {
            var sacuvan = await userManager.GetAuthenticationTokenAsync(user, Provajder, Ime);
            if (string.IsNullOrEmpty(sacuvan)) return;

            var ok = await apple.OpozoviAsync(_zastita.Unprotect(sacuvan));
            await userManager.RemoveAuthenticationTokenAsync(user, Provajder, Ime);

            if (!ok)
                logger.LogWarning("Apple prijava za {UserId} nije poništena kod Apple-a (token ipak obrisan).", user.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Poništavanje Apple prijave nije uspelo za {UserId}.", user.Id);
        }
    }
}
