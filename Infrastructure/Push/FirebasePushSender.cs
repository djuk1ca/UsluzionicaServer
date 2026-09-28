using System.Text;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace UsluzionicaServer.Infrastructure.Push;

/// <summary>
/// Slanje preko Firebase Cloud Messaging-a.
///
/// SINGLETON, I TO NIJE STVAR UKUSA.
/// <c>FirebaseApp.Create</c> sme da se izvrši tačno jednom po procesu — drugi
/// poziv baca izuzetak. <c>FirebaseMessaging</c> je posle toga thread-safe, pa
/// jedna instanca opslužuje sve zahteve.
/// </summary>
public sealed class FirebasePushSender : IPushSender
{
    /// <summary>
    /// Mora se poklapati sa kanalom koji klijent registruje u <c>MauiProgram</c>
    /// i sa <c>default_notification_channel_id</c> iz <c>AndroidManifest.xml</c>.
    ///
    /// Ako se raziđu, notifikacija u pozadini pada u kanal koji ne postoji i
    /// Android je tiho odbacuje — bez greške bilo gde.
    /// </summary>
    public const string ChannelId = "usluzionica_default";

    private readonly ILogger<FirebasePushSender> _logger;
    private readonly FirebaseMessaging           _messaging;
    /// <summary>
    /// Prima ključ kao SIROV JSON ili kao base64.
    ///
    /// Base64 postoji zbog `.env`. Service account fajl je višelinijski JSON pun
    /// navodnika, kosih crta i escape sekvenci unutar `private_key`. I sažet u
    /// jedan red ume da se sudari sa interpolacijom promenljivih ili sa načinom
    /// na koji pojedini alati čitaju `.env` — a kvar se tada vidi tek kad prva
    /// notifikacija ne stigne. Base64 nema nijedan znak koji ti parseri tumače.
    ///
    /// Oba oblika se primaju da bi lokalni razvoj mogao da zalepi običan JSON u
    /// `appsettings.Local.json`, gde te opasnosti nema.
    /// </summary>
    private static string RazresiKljuc(string vrednost)
    {
        var t = vrednost.Trim();

        // JSON uvek pocinje vitrastom zagradom; base64 nikad.
        if (t.StartsWith('{'))
            return t;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(t));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "Firebase:ServiceAccountJson nije ni JSON ni ispravan base64. " +
                "Ocekuje se ceo service account fajl iz Firebase konzole.");
        }
    }

    public FirebasePushSender(string serviceAccountJson, ILogger<FirebasePushSender> logger)
    {
        _logger = logger;

        // FirebaseApp.DefaultInstance je null dok se ne napravi. Provera postoji
        // zbog integracionih testova, koji dižu WebApplicationFactory više puta
        // u istom procesu — bez nje bi drugi test pao na „already exists".
        // CS0618 je potisnut namerno.
        //
        // Zamena koju poruka predlaze, `CredentialFactory`, nudi samo `FromFile`.
        // Nas kljuc stize kao STRING iz `.env`, isto kao SMTP lozinka - pisanje
        // tajne na disk samo da bi se zadovoljio potpis metoda bilo bi gore od
        // samog upozorenja.
#pragma warning disable CS0618
        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromStream(
                new MemoryStream(Encoding.UTF8.GetBytes(RazresiKljuc(serviceAccountJson))))
#pragma warning restore CS0618
        });

        _messaging = FirebaseMessaging.GetMessaging(app);
    }

    public async Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> tokens,
        PushMessage           message,
        CancellationToken     ct = default)
    {
        if (tokens.Count == 0)
            return [];

        // Notifikacije iz ISTOG razgovora se smenjuju umesto da se gomilaju.
        // Deset poruka iz jednog ćaskanja je jedan red u listi obaveštenja, a ne
        // deset. Vrste bez reference dobijaju „0", pa se npr. dve različite
        // prihvaćene ponude i dalje vide odvojeno po vrsti.
        var kljucGrupe = $"{message.Kind}:{message.ReferenceId?.ToString() ?? "0"}";

        // CS0618 je potisnut namerno.
        //
        // Poruka predlaze `Fid`, ali to je Firebase Installation ID - DRUGI
        // identifikator. Klijentski SDK vraca registracioni token, pa bi upis u
        // `Fid` slao na nepostojecu adresu. `Token` je i dalje ispravno polje za
        // ovaj tok; zastarelost je najava, ne promena ponasanja.
#pragma warning disable CS0618
        Message ZaToken(string token) => new()
        {
            Token = token,

            // `Notification` deo, a ne samo `data` — namerno.
            //
            // Sa njim Android SAM prikaže notifikaciju dok je aplikacija u
            // pozadini ili ugašena, dakle u jedinom slučaju u kom SignalR ne
            // radi. Kad je aplikacija u prvom planu, sistem je NE prikazuje nego
            // je prosledi kodu — a tada je SignalR već digao lokalnu, pa je
            // klijent preskače. Tako nema duplog prikaza ni u jednom stanju.
            //
            // Sa samim `data` payload-om ugašena aplikacija ne bi prikazala
            // ništa dok je korisnik sam ne otvori — što je upravo problem koji
            // push treba da reši.
            Notification = new FirebaseAdmin.Messaging.Notification
            {
                Title = message.Title,
                Body  = message.Body
            },

            // Vrednosti moraju biti string — FCM ne prima druge tipove.
            Data = new Dictionary<string, string>
            {
                ["kind"]           = message.Kind,
                ["referenceId"]    = message.ReferenceId?.ToString() ?? string.Empty,
                ["notificationId"] = message.NotificationId.ToString()
            },

            Android = new AndroidConfig
            {
                // Bez High prioriteta Doze režim ume da odloži isporuku za sate.
                // Za poruku u razgovoru to je isto kao da nije ni stigla.
                Priority     = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = ChannelId,
                    Tag       = kljucGrupe
                }
            },

            Apns = new ApnsConfig
            {
                Aps = new Aps
                {
                    Sound = "default",

                    // iOS ekvivalent Android-ovog `Tag`.
                    ThreadId = kljucGrupe
                }
            }
        };
#pragma warning restore CS0618

        // Jedna poruka po tokenu, pa `SendEachAsync`.
        //
        // `MulticastMessage` je u FirebaseAdmin 3.x označen kao zastareo. Ovako
        // se dobija i uredniji rezultat: odgovori stižu u ISTOM redosledu kao
        // ulazni tokeni, pa se mrtav token prepoznaje po indeksu.
        var poruke  = tokens.Select(ZaToken).ToList();
        var odgovor = await _messaging.SendEachAsync(poruke, ct);

        if (odgovor.FailureCount == 0)
            return [];

        var mrtvi = new List<string>();

        for (var i = 0; i < odgovor.Responses.Count; i++)
        {
            var r = odgovor.Responses[i];
            if (r.IsSuccess) continue;

            var kod = r.Exception?.MessagingErrorCode;

            // Dve greške znače da token više ne postoji — aplikacija je
            // deinstalirana, ili token pripada drugom Firebase projektu. Takav
            // red se briše, jer je svako dalje slanje na njega uzaludan poziv
            // koji usporava isporuku svima ostalima.
            //
            // Sve ostale greške (mreža, kvota, privremeni pad FCM-a) su prolazne
            // i token se ZADRŽAVA — brisanje bi trajno oduzelo notifikacije
            // korisniku zbog trenutnog kvara.
            if (kod is MessagingErrorCode.Unregistered or MessagingErrorCode.SenderIdMismatch)
            {
                mrtvi.Add(tokens[i]);
            }
            else
            {
                _logger.LogWarning(
                    "FCM nije isporučio notifikaciju {NotificationId} ({Kind}): {Kod}",
                    message.NotificationId, message.Kind, kod?.ToString() ?? "nepoznato");
            }
        }

        return mrtvi;
    }
}
