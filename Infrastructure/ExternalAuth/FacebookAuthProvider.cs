using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace UsluzionicaServer.Infrastructure.ExternalAuth;

/// <summary>
/// Prijava preko Facebook-a (Graph API).
///
/// Traže se samo <c>public_profile</c> i <c>email</c>. Svaka dozvola preko toga
/// traži posebno obrazloženje u Meta App Review-u i podatak koji moramo da
/// čuvamo i štitimo, a ne koristimo ga.
/// </summary>
public sealed class FacebookAuthProvider(HttpClient http, IConfiguration config) : IExternalAuthProvider
{
    public string Name => "facebook";

    private string AppId     => config["Facebook:AppId"]     ?? string.Empty;
    private string AppSecret => config["Facebook:AppSecret"] ?? string.Empty;

    /// <summary>
    /// Verzija Graph API-ja. Meta gasi verziju oko dve godine posle izlaska —
    /// posle toga pozivi i dalje rade, ali na najstarijoj živoj verziji, pa se
    /// ponašanje menja bez ijedne izmene kod nas. Menja se kroz konfiguraciju.
    /// </summary>
    private string Version => config["Facebook:GraphVersion"] is { Length: > 0 } v ? v : "v24.0";

    public bool IsConfigured => AppId.Length > 0 && AppSecret.Length > 0;

    public string BuildAuthorizeUrl(string state, string redirectUri) =>
        $"https://www.facebook.com/{Version}/dialog/oauth" +
        $"?client_id={Uri.EscapeDataString(AppId)}" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}" +
        "&response_type=code" +
        $"&scope={Uri.EscapeDataString("public_profile,email")}";

    public async Task<ExternalIdentity> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        // ── 1. Kod → access token ──────────────────────────────────────────
        var tokenUrl =
            $"https://graph.facebook.com/{Version}/oauth/access_token" +
            $"?client_id={Uri.EscapeDataString(AppId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&client_secret={Uri.EscapeDataString(AppSecret)}" +
            $"&code={Uri.EscapeDataString(code)}";

        using var tokenResp = await http.GetAsync(tokenUrl, ct);
        if (!tokenResp.IsSuccessStatusCode)
            throw new ExternalAuthException(
                $"Facebook odbio razmenu koda: {(int)tokenResp.StatusCode} " +
                await tokenResp.Content.ReadAsStringAsync(ct));

        var token = await tokenResp.Content.ReadFromJsonAsync<TokenOdgovor>(ct);
        if (string.IsNullOrEmpty(token?.AccessToken))
            throw new ExternalAuthException("Facebook nije vratio access token.");

        // ── 2. Ko je korisnik ──────────────────────────────────────────────
        // appsecret_proof dokazuje da poziv šalje server koji zna App Secret.
        // Ako token ikad procuri, sam po sebi ne može da se iskoristi protiv
        // Graph API-ja u ime naše aplikacije (uz uključen „Require App Secret").
        var proof = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(AppSecret),
            Encoding.UTF8.GetBytes(token.AccessToken))).ToLowerInvariant();

        var meUrl =
            $"https://graph.facebook.com/{Version}/me" +
            "?fields=id,name,email" +
            $"&access_token={Uri.EscapeDataString(token.AccessToken)}" +
            $"&appsecret_proof={proof}";

        using var meResp = await http.GetAsync(meUrl, ct);
        if (!meResp.IsSuccessStatusCode)
            throw new ExternalAuthException(
                $"Facebook odbio /me: {(int)meResp.StatusCode} " +
                await meResp.Content.ReadAsStringAsync(ct));

        var me = await meResp.Content.ReadFromJsonAsync<MeOdgovor>(ct);
        if (string.IsNullOrEmpty(me?.Id))
            throw new ExternalAuthException("Facebook nije vratio id korisnika.");

        return new ExternalIdentity(
            Provider:    "Facebook",
            ProviderKey: me.Id,
            Email:       string.IsNullOrWhiteSpace(me.Email) ? null : me.Email.Trim(),

            // NAMERNO false, iako Facebook uglavnom vraća potvrđen mejl.
            //
            // Graph API to ne GARANTUJE poljem kao Google (`email_verified`).
            // Posledica „false": Facebook prijava ne povezuje se sa POSTOJEĆIM
            // nalogom po mejlu — korisnik tada dobija poruku da se prijavi
            // načinom kojim je nalog napravio. Posledica „true" uz nepotvrđen
            // mejl bila bi preuzimanje tuđeg naloga. Prvo je neprijatnost,
            // drugo je propust.
            EmailVerified: false,
            Name:        me.Name?.Trim() ?? string.Empty);
    }

    private sealed record TokenOdgovor(
        [property: JsonPropertyName("access_token")] string? AccessToken);

    private sealed record MeOdgovor(
        [property: JsonPropertyName("id")]    string? Id,
        [property: JsonPropertyName("name")]  string? Name,
        [property: JsonPropertyName("email")] string? Email);
}
