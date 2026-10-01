namespace UsluzionicaServer.Infrastructure.ExternalAuth;

/// <summary>
/// Jedan OAuth provajder (Google, Facebook).
///
/// Tok je Authorization Code, i to na SERVERU, a ne u aplikaciji: razmena koda
/// traži tajnu aplikacije (App Secret / Client Secret), a tajna ugrađena u APK
/// nije tajna — svako je izvuče iz fajla za nekoliko minuta.
/// </summary>
public interface IExternalAuthProvider
{
    /// <summary>Ime u ruti: <c>/api/auth/{Name}/start</c>. Mala slova.</summary>
    string Name { get; }

    /// <summary>
    /// Da li su podešeni ključevi. Nepodešen provajder se ne nudi — isti obrazac
    /// kao FCM: razvoj i testovi rade bez produkcijskih tajni.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Adresa provajderove stranice za prijavu.</summary>
    string BuildAuthorizeUrl(string state, string redirectUri);

    /// <summary>
    /// Menja jednokratni kod za identitet korisnika. Baca
    /// <see cref="ExternalAuthException"/> ako provajder odbije.
    /// </summary>
    Task<ExternalIdentity> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct);
}
