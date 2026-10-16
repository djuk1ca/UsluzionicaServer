namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Vrsta poruke u četu. Čuva se kao string.
///
/// Obična poruka je <see cref="Text"/>. Ostale vrste klijent crta drugačije
/// (kartica umesto balona), ali kroz isti tok — isto šifrovanje, isti rok od
/// 14 dana, isto obaveštenje.
/// </summary>
public enum MessageKind
{
    Text,

    /// <summary>
    /// Javljanje na posao. Tekst poruke je poruka iz javljanja, a kartica
    /// (naslov posla, ponuda) se čita preko <c>JobApplicationId</c>.
    /// </summary>
    JobApplication,

    /// <summary>
    /// Slika (<c>Message.Attachment</c>). Tekst poruke je opcioni opis; stari
    /// klijenti umesto praznog balona dobijaju rezervni tekst (ChatSender).
    /// </summary>
    Image
}
