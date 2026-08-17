using System.Collections.Concurrent;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Hvata "poslate" emailove umesto da otvara SMTP konekciju.
///
/// Dve uloge:
///  1. Hermetičnost — bez ovoga bi svaki test koji registruje korisnika
///     pokušavao pravu konekciju ka smtp serveru (sporo, pada u CI-ju).
///  2. Jedini put do koda za reset lozinke — u bazi se čuva SHA-256 heš,
///     pa test čist kod može dobiti samo presretanjem poruke.
///
/// ConcurrentBag jer host može slati iz više niti (npr. u konkurentnom testu).
/// </summary>
public sealed class FakeEmailService : IEmailService
{
    public sealed record SentEmail(string Kind, string To, string FullName, string Payload);

    private readonly ConcurrentBag<SentEmail> _sent = [];

    public IReadOnlyCollection<SentEmail> Sent => _sent;

    public Task SendVerificationEmailAsync(string toEmail, string fullName, string verifyUrl)
    {
        _sent.Add(new SentEmail("verification", toEmail, fullName, verifyUrl));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string fullName, string resetCode)
    {
        _sent.Add(new SentEmail("password-reset", toEmail, fullName, resetCode));
        return Task.CompletedTask;
    }

    public void Clear() => _sent.Clear();

    // ── Pomoćni upiti za testove ───────────────────────────────────────────

    /// <summary>Verifikacioni URL poslat na datu adresu (poslednji).</summary>
    public string? LastVerificationUrlFor(string email) =>
        _sent.Where(e => e.Kind == "verification" &&
                         e.To.Equals(email, StringComparison.OrdinalIgnoreCase))
             .Select(e => e.Payload)
             .LastOrDefault();

    /// <summary>Šestocifreni kod za reset lozinke poslat na datu adresu (poslednji).</summary>
    public string? LastResetCodeFor(string email) =>
        _sent.Where(e => e.Kind == "password-reset" &&
                         e.To.Equals(email, StringComparison.OrdinalIgnoreCase))
             .Select(e => e.Payload)
             .LastOrDefault();
}
