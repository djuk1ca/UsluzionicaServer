namespace UsluzionicaServer.Infrastructure.Push;

/// <summary>
/// Jedna notifikacija, spremna za slanje na uređaj.
///
/// Naslov i telo se NE grade ovde. Oni već postoje — svako od 13 mesta koje
/// zove <c>NotificationService.SendAsync</c> gradi svoj tekst, i taj tekst je
/// već dinamičan: koliko tokena, ko šalje poruku i šta u njoj piše, koliko
/// zvezdica nosi recenzija. Ovaj tip ih samo prenosi dalje.
/// </summary>
/// <param name="NotificationId">
/// ID reda iz tabele <c>Notifications</c>.
///
/// Ide u payload da bi klijent znao KOJA je ovo notifikacija — za deep link i
/// za to da se ista ne prebroji dvaput u badge-u.
///
/// Dupli PRIKAZ se ne sprečava ovim brojem nego stanjem aplikacije: dok je u
/// prvom planu, Android FCM notifikaciju ne prikazuje sam nego je prosledi
/// kodu, a tada je SignalR već digao lokalnu — pa je klijent preskače. U
/// pozadini SignalR ne radi, prikazuje je sistem, i druge nema.
/// </param>
/// <param name="Kind">Vrsta, npr. <c>NewMessage</c> — klijent po njoj bira gde da odvede korisnika.</param>
/// <param name="ReferenceId">ID razgovora, rezervacije ili oglasa — drugi deo deep link-a.</param>
public sealed record PushMessage(
    int     NotificationId,
    string  Kind,
    string  Title,
    string  Body,
    int?    ReferenceId);

/// <summary>
/// Slanje push notifikacija na uređaje.
///
/// Postoji kao interfejs iz istog razloga kao <c>IImageModerator</c>: bez
/// konfigurisanog Firebase ključa aplikacija mora da radi normalno, a testovi
/// ne smeju da zavise od mreže. Zato dve implementacije — prava i prazna.
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Šalje notifikaciju na sve date tokene i vraća one koje je FCM odbio kao
    /// nepostojeće.
    ///
    /// VRAĆA MRTVE TOKENE UMESTO DA IH SAM OBRIŠE — namerno.
    ///
    /// Implementacija je <b>singleton</b>, jer <c>FirebaseApp.Create</c> sme da
    /// se izvrši tačno jednom po procesu. <c>AppDbContext</c> je <b>scoped</b>.
    /// Singleton koji drži scoped zavisnost je klasičan način da se dobije
    /// odloženi kontekst i greške koje se ne reprodukuju.
    ///
    /// Zato brisanje radi pozivalac, koji kontekst ionako već ima.
    /// </summary>
    Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> tokens,
        PushMessage           message,
        CancellationToken     ct = default);
}
