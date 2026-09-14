using System.Text.Json;

namespace UsluzionicaServer.Infrastructure.Media;

/// <summary>
/// Provera slika preko Google Cloud Vision SafeSearch-a.
///
/// ZAŠTO GOOGLE, a ne lokalni ONNX model:
/// Prvih 1.000 slika mesečno je besplatno — za aplikaciju koja tek kreće to je
/// oko 200 novih oglasa sa po pet slika. Lokalni model (NsfwSharp, NudeNet)
/// ne bi koštao ništa po slici, ali bi na jeftinom Hetzner VPS-u trošio CPU
/// koji deli sa bazom i API-jem, i nosio bi ~50 MB modela u Docker slici.
///
/// ZAŠTO REST, a ne Google.Cloud.Vision.V1 paket:
/// Paket povlači gRPC i ceo Google.Api lanac zavisnosti zbog jednog poziva
/// koji je običan POST sa base64 telom. Ovako nema nove zavisnosti.
///
/// KLJUČ NE SME U REPO. Čita se iz konfiguracije
/// (<c>ImageModeration:ApiKey</c>), koja na produkciji dolazi iz .env-a.
/// </summary>
public sealed class CloudVisionImageModerator(
    HttpClient                             http,
    IConfiguration                         config,
    ILogger<CloudVisionImageModerator>     logger) : IImageModerator
{
    private const string Endpoint = "https://vision.googleapis.com/v1/images:annotate";

    /// <summary>
    /// Gornja granica čekanja. Namerno kratka: korisnik čeka da mu se slika
    /// otpremi, a fail-open ionako znači da spor odgovor ne menja ishod —
    /// samo odlaže ono što će svakako proći na ručni pregled.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    public async Task<(ImageVerdict, string?)> ScanAsync(
        Stream image, CancellationToken ct = default)
    {
        var kljuc = config["ImageModeration:ApiKey"];

        if (string.IsNullOrWhiteSpace(kljuc))
        {
            // Pogrešno podešavanje ne sme da obori upload, ali mora da se vidi
            // u logovima — inače bi moderacija tiho bila isključena mesecima.
            logger.LogWarning(
                "ImageModeration:ApiKey nije podešen — slike se ne proveravaju automatski.");
            return (ImageVerdict.Review, "Provera nije podešena.");
        }

        try
        {
            image.Position = 0;
            using var ms = new MemoryStream();
            await image.CopyToAsync(ms, ct);
            var base64 = Convert.ToBase64String(ms.ToArray());

            // Pozicija se vraća jer pozivalac isti stream koristi za upis na disk.
            image.Position = 0;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var telo = new
            {
                requests = new[]
                {
                    new
                    {
                        image    = new { content = base64 },
                        features = new[] { new { type = "SAFE_SEARCH_DETECTION" } }
                    }
                }
            };

            var odgovor = await http.PostAsJsonAsync($"{Endpoint}?key={kljuc}", telo, cts.Token);

            if (!odgovor.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Cloud Vision vratio {Status} — slika ide na ručni pregled.",
                    (int)odgovor.StatusCode);
                return (ImageVerdict.Review, $"Provera nije uspela (HTTP {(int)odgovor.StatusCode}).");
            }

            using var dok = JsonDocument.Parse(await odgovor.Content.ReadAsStringAsync(cts.Token));

            var oznake = dok.RootElement
                .GetProperty("responses")[0]
                .GetProperty("safeSearchAnnotation");

            var adult = oznake.GetProperty("adult").GetString() ?? "UNKNOWN";
            var racy  = oznake.GetProperty("racy") .GetString() ?? "UNKNOWN";

            return Presudi(adult, racy);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Isteklo NAŠE vreme, ne korisnikovo otkazivanje.
            logger.LogWarning("Cloud Vision nije odgovorio u {Sekundi}s.", Timeout.TotalSeconds);
            return (ImageVerdict.Review, "Provera je istekla.");
        }
        catch (Exception ex)
        {
            // FAIL-OPEN. Slika prolazi, ali ostavlja trag za ručni pregled.
            // Fail-closed bi značio da pad tuđeg servisa obara mogućnost
            // postavljanja oglasa na celoj aplikaciji.
            logger.LogError(ex, "Greška pri proveri slike — slika ide na ručni pregled.");
            return (ImageVerdict.Review, "Provera nije uspela.");
        }
    }

    /// <summary>
    /// Mapira SafeSearch nivoe u presudu.
    ///
    /// SafeSearch vraća pet nivoa: VERY_UNLIKELY, UNLIKELY, POSSIBLE, LIKELY,
    /// VERY_LIKELY. Prag je namerno visok.
    ///
    /// <c>adult</c> je eksplicitna golotinja; <c>racy</c> je „izazovno" —
    /// oskudna odeća, poze. Racy sam po sebi NIKAD ne odbija upload, jer
    /// fotografija sa bazena ili iz teretane legitimno pada u tu kategoriju.
    /// </summary>
    private static (ImageVerdict, string?) Presudi(string adult, string racy)
    {
        var detalji = $"adult={adult}, racy={racy}";

        if (adult == "VERY_LIKELY")
            return (ImageVerdict.Reject, detalji);

        if (adult == "LIKELY" || racy == "VERY_LIKELY")
            return (ImageVerdict.Review, detalji);

        return (ImageVerdict.Allow, null);
    }
}
