using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace UsluzionicaServer.Services;

/// <summary>
/// Šalje transakcione emailove (verifikacija, reset lozinke) putem SMTP-a (MailKit).
/// Konfiguracija se čita iz appsettings.json → "Email" sekcija.
/// </summary>
public sealed class EmailService(IConfiguration config, ILogger<EmailService> logger)
{
    private readonly string _host     = config["Email:Host"]     ?? "smtp.gmail.com";
    private readonly int    _port     = int.Parse(config["Email:Port"] ?? "587");
    private readonly string _username = config["Email:Username"] ?? "";
    private readonly string _password = config["Email:Password"] ?? "";
    private readonly string _from     = config["Email:From"]     ?? "noreply@usluzionica.rs";

    // ── Javni API ──────────────────────────────────────────────────────────

    public Task SendVerificationEmailAsync(string toEmail, string fullName, string verifyUrl)
        => SendAsync(
            to:      toEmail,
            subject: "Potvrdi svoju Uslužionica adresu",
            html:    BuildVerificationHtml(fullName, verifyUrl));

    // ── Interni helper ─────────────────────────────────────────────────────

    private async Task SendAsync(string to, string subject, string html)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(_from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body    = new TextPart("html") { Text = html };

        try
        {
            using var client = new SmtpClient();

            // StartTls = port 587 (Gmail, Outlook, Mailtrap...)
            await client.ConnectAsync(_host, _port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_username, _password);
            await client.SendAsync(message);
            await client.DisconnectAsync(quit: true);

            logger.LogInformation("Email poslat na {To} | {Subject}", to, subject);
        }
        catch (Exception ex)
        {
            // Ne blokiramo registraciju ako SMTP padne — samo logujemo
            logger.LogError(ex, "Slanje emaila na {To} nije uspelo.", to);
            throw; // prosleđujemo da bi caller odlučio šta radi s greškom
        }
    }

    // ── HTML template ──────────────────────────────────────────────────────

    private static string BuildVerificationHtml(string fullName, string verifyUrl) => $"""
        <!DOCTYPE html>
        <html lang="sr">
        <head><meta charset="UTF-8"></head>
        <body style="font-family:Inter,Arial,sans-serif;background:#F7FAFC;margin:0;padding:32px;">
          <div style="max-width:480px;margin:0 auto;background:#fff;border-radius:16px;
                      padding:40px;box-shadow:0 2px 8px rgba(0,0,0,0.08);">
            <h2 style="color:#0B1220;margin-top:0;">Dobrodošao/la, {fullName}! 👋</h2>
            <p style="color:#334155;line-height:1.6;">
              Samo još jedan korak — potvrdi svoju email adresu kako bismo aktivirali tvoj nalog.
            </p>
            <a href="{verifyUrl}"
               style="display:inline-block;margin:24px 0;padding:14px 32px;
                      background:#2F6BFF;color:#fff;text-decoration:none;
                      border-radius:12px;font-weight:700;font-size:15px;">
              Potvrdi email adresu
            </a>
            <p style="color:#94A3B8;font-size:12px;margin-bottom:0;">
              Link ističe za 24 sata. Ako nisi ti kreirao/la nalog, ignoriši ovaj email.
            </p>
          </div>
        </body>
        </html>
        """;
}
