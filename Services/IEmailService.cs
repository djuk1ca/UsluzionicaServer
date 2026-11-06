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

    /// <summary>
    /// Mejl listi čekanja (npr. „stigli smo na App Store"). Poruka je već
    /// sklopljena (<see cref="ListaCekanjaMejlSablon"/>), pa dry-run i pravo
    /// slanje pokazuju isti tekst. Uz HTML ide i tekstualna verzija, a link za
    /// odjavu ide i u zaglavlje List-Unsubscribe.
    /// </summary>
    Task SendListaCekanjaAsync(MejlListeCekanja mejl);
}

/// <param name="To">Adresa sa liste.</param>
/// <param name="Subject">Naslov.</param>
/// <param name="Html">HTML verzija.</param>
/// <param name="Text">Tekstualna verzija (multipart/alternative).</param>
/// <param name="OdjavaUrl">Odjava jednim klikom — i u telu i u zaglavlju List-Unsubscribe.</param>
public sealed record MejlListeCekanja(string To, string Subject, string Html, string Text, string OdjavaUrl);
