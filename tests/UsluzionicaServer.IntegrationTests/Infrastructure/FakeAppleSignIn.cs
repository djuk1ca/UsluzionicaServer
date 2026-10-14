using System.Collections.Concurrent;
using UsluzionicaServer.Infrastructure.Apple;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Sign in with Apple bez Apple-a: test unapred kaže koji identityToken
/// predstavlja kog korisnika, a beleži se šta je poništeno.
///
/// Pravi AppleSignIn bi tražio mrežu (Apple-ovi javni ključevi) i token koji
/// je potpisao Apple — u testu nemoguće. Logika provere tokena je pokrivena
/// unit testovima (AppleTests); ovde se proverava tok kroz server.
/// </summary>
public sealed class FakeAppleSignIn : IAppleSignIn
{
    private readonly ConcurrentDictionary<string, AppleIdentitet> _tokeni = new();

    public bool Podesen { get; set; } = true;

    /// <summary>Refresh tokeni za koje je pozvan Apple-ov /auth/revoke.</summary>
    public ConcurrentBag<string> Opozvani { get; } = [];

    /// <summary>Registruje identityToken; kod za njega daje refresh token „refresh-{token}".</summary>
    public void Nauci(string identityToken, AppleIdentitet identitet) => _tokeni[identityToken] = identitet;

    public Task<AppleIdentitet?> ProveriAsync(string identityToken, CancellationToken ct = default) =>
        Task.FromResult(_tokeni.TryGetValue(identityToken, out var i) ? i : null);

    public Task<string?> RefreshTokenAsync(string authorizationCode, CancellationToken ct = default) =>
        Task.FromResult<string?>(string.IsNullOrEmpty(authorizationCode) ? null : $"refresh-{authorizationCode}");

    public Task<bool> OpozoviAsync(string refreshToken, CancellationToken ct = default)
    {
        Opozvani.Add(refreshToken);
        return Task.FromResult(true);
    }
}
