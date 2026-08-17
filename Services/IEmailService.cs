namespace UsluzionicaServer.Services;

/// <summary>
/// Slanje transakcionih emailova. Postoji kao interfejs iz dva razloga:
///
/// 1. To je jedina spoljna I/O zavisnost u toku registracije i reseta lozinke.
///    Bez interfejsa bi svaki test pokušavao pravu SMTP konekciju — sporo,
///    nepouzdano, i nemoguće u CI-ju bez pristupa mreži.
///
/// 2. Kod za reset lozinke se u bazi čuva HEŠIRAN (vidi PasswordResetCode).
///    Test drugačije ne može doći do čistog koda — jedini način je da presretne
///    poslatu poruku. Zato test implementacija hvata pozive u listu.
/// </summary>
public interface IEmailService
{
    Task SendVerificationEmailAsync(string toEmail, string fullName, string verifyUrl);

    Task SendPasswordResetEmailAsync(string toEmail, string fullName, string resetCode);
}
