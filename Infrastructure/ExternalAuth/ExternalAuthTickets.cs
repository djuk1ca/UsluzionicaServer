using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace UsluzionicaServer.Infrastructure.ExternalAuth;

/// <summary>
/// Kratkoživeći podaci koji moraju preživeti put do provajdera i nazad.
///
/// ZAŠTO DATA PROTECTION, A NE REDIS ILI TABELA
///
/// Tokom prijave server tri puta nešto „predaje" i kasnije mora da prepozna:
/// <c>state</c> ide Google-u/Facebook-u, kod rezultata ide aplikaciji, a
/// karta za registraciju ide ekranu „Dovrši nalog". Sve troje se ovde
/// potpisuje i šifruje ključevima koji su već deljeni preko Redisa (vidi
/// Program.cs, Data Protection).
///
///   • Nijedan red u bazi, nijedna migracija, ništa za čišćenje.
///   • Redis keš u ovom projektu je namerno fail-open — da prijava zavisi od
///     njega, pad keša bi značio da niko ne može da se prijavi.
///   • Rok važenja je UGRAĐEN u sam token (<see cref="ITimeLimitedDataProtector"/>),
///     pa ga ni izmenjen ni istekao token ne može zaobići.
///
/// Svrha (purpose) je različita za sve tri vrste — `state` ne može da se
/// podmetne kao kod rezultata, iako je isti mehanizam.
/// </summary>
public sealed class ExternalAuthTickets(IDataProtectionProvider dataProtection)
{
    /// <summary>Koliko korisnik sme da provede na Google-ovoj/Facebook-ovoj stranici.</summary>
    public static readonly TimeSpan StateLifetime  = TimeSpan.FromMinutes(10);

    /// <summary>Od povratka u aplikaciju do razmene — aplikacija to radi odmah.</summary>
    public static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Koliko korisnik ima da popuni „Dovrši nalog".</summary>
    public static readonly TimeSpan SignupLifetime = TimeSpan.FromMinutes(30);

    private readonly ITimeLimitedDataProtector _state =
        dataProtection.CreateProtector("ExternalAuth.State.v1").ToTimeLimitedDataProtector();

    private readonly ITimeLimitedDataProtector _result =
        dataProtection.CreateProtector("ExternalAuth.Result.v1").ToTimeLimitedDataProtector();

    private readonly ITimeLimitedDataProtector _signup =
        dataProtection.CreateProtector("ExternalAuth.Signup.v1").ToTimeLimitedDataProtector();

    /// <summary>Šta putuje kroz provajdera kao <c>state</c>.</summary>
    public sealed record StateData(string Provider, string Challenge, string? ReferralCode);

    /// <summary>
    /// Šta aplikacija dobija posle povratka: ili već poznat nalog (<see cref="UserId"/>),
    /// ili identitet za koji tek treba napraviti nalog (<see cref="Identity"/>).
    /// </summary>
    public sealed record ResultData(
        string            Challenge,
        string?           UserId,
        ExternalIdentity? Identity,
        string?           ReferralCode);

    /// <summary>Karta za ekran „Dovrši nalog".</summary>
    public sealed record SignupData(ExternalIdentity Identity, string? ReferralCode);

    public string ProtectState(StateData data)   => Protect(_state,  data, StateLifetime);
    public string ProtectResult(ResultData data) => Protect(_result, data, ResultLifetime);
    public string ProtectSignup(SignupData data) => Protect(_signup, data, SignupLifetime);

    /// <summary>Null ako je token izmenjen, istekao ili druge vrste.</summary>
    public StateData?  UnprotectState(string token)  => Unprotect<StateData>(_state,  token);
    public ResultData? UnprotectResult(string token) => Unprotect<ResultData>(_result, token);
    public SignupData? UnprotectSignup(string token) => Unprotect<SignupData>(_signup, token);

    // ── PKCE ───────────────────────────────────────────────────────────────
    //
    // Povratak u aplikaciju ide preko `usluzionica://auth`. Tu šemu može da
    // registruje i TUĐA aplikacija na istom telefonu, pa kod rezultata sam po
    // sebi nije dovoljan: zlonamerna aplikacija bi ga uhvatila i razmenila za
    // naše tokene.
    //
    // Zato aplikacija na početku izmisli tajnu (verifier) i pošalje samo njen
    // SHA-256 otisak (challenge). Kod se razmenjuje isključivo uz originalnu
    // tajnu — a nju ima samo aplikacija koja je prijavu i započela.

    /// <summary>Challenge je base64url SHA-256 otiska: tačno 43 znaka.</summary>
    public static bool IsValidChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(IsBase64UrlChar);

    /// <summary>Verifier po RFC 7636: 43–128 znakova iz neznatno šireg skupa.</summary>
    public static bool IsValidVerifier(string? verifier) =>
        verifier is { Length: >= 43 and <= 128 } &&
        verifier.All(c => IsBase64UrlChar(c) || c is '.' or '~');

    public static bool VerifierMatches(string verifier, string challenge)
    {
        var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Poređenje u konstantnom vremenu — obično `==` odaje kroz trajanje
        // koliko se znakova poklopilo.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(challenge));
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsBase64UrlChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static string Protect<T>(ITimeLimitedDataProtector p, T data, TimeSpan lifetime) =>
        p.Protect(JsonSerializer.Serialize(data), lifetime);

    private static T? Unprotect<T>(ITimeLimitedDataProtector p, string token) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(p.Unprotect(token));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            // Izmenjen, istekao ili potpisan drugim ključem — sve troje znači
            // isto: prijava mora da krene iz početka.
            return null;
        }
    }
}
