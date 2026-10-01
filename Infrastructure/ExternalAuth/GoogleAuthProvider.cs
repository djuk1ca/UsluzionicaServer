using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth;

namespace UsluzionicaServer.Infrastructure.ExternalAuth;

/// <summary>
/// Prijava preko Google-a (OpenID Connect).
///
/// Opsezi su <c>openid email profile</c> — „neosetljivi" po Google-ovoj
/// klasifikaciji, pa aplikacija ne prolazi Google-ovu bezbednosnu proveru.
/// </summary>
public sealed class GoogleAuthProvider(HttpClient http, IConfiguration config) : IExternalAuthProvider
{
    public string Name => "google";

    private string ClientId     => config["Google:ClientId"]     ?? string.Empty;
    private string ClientSecret => config["Google:ClientSecret"] ?? string.Empty;

    public bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    public string BuildAuthorizeUrl(string state, string redirectUri) =>
        "https://accounts.google.com/o/oauth2/v2/auth" +
        $"?client_id={Uri.EscapeDataString(ClientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString("openid email profile")}" +
        // Bez ovoga Google tiho uzme poslednji korišćeni nalog. Na telefonu sa
        // privatnim i poslovnim nalogom korisnik bi završio u pogrešnom — i ne
        // bi imao način da izabere drugi osim da se odjavi iz Google-a.
        "&prompt=select_account";

    public async Task<ExternalIdentity> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        using var tokenResp = await http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"]          = code,
                ["client_id"]     = ClientId,
                ["client_secret"] = ClientSecret,
                ["redirect_uri"]  = redirectUri,
                ["grant_type"]    = "authorization_code"
            }),
            ct);

        if (!tokenResp.IsSuccessStatusCode)
            throw new ExternalAuthException(
                $"Google odbio razmenu koda: {(int)tokenResp.StatusCode} " +
                await tokenResp.Content.ReadAsStringAsync(ct));

        var token = await tokenResp.Content.ReadFromJsonAsync<TokenOdgovor>(ct);
        if (string.IsNullOrEmpty(token?.IdToken))
            throw new ExternalAuthException("Google nije vratio id_token.");

        // id_token je stigao direktno sa Google-ovog servera preko TLS-a, pa bi
        // po OIDC specifikaciji potpis smeo da se preskoči. Ipak se proverava:
        // potpis, izdavalac, rok i — najvažnije — publika (`aud`). Bez provere
        // publike prošao bi i token izdat nekoj DRUGOJ aplikaciji.
        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                token.IdToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = [ClientId] });
        }
        catch (InvalidJwtException ex)
        {
            throw new ExternalAuthException("Google id_token nije prošao proveru.", ex);
        }

        return new ExternalIdentity(
            Provider:      "Google",
            ProviderKey:   payload.Subject,
            Email:         string.IsNullOrWhiteSpace(payload.Email) ? null : payload.Email.Trim(),
            EmailVerified: payload.EmailVerified,
            Name:          payload.Name?.Trim() ?? string.Empty);
    }

    private sealed record TokenOdgovor(
        [property: JsonPropertyName("id_token")] string? IdToken);
}
