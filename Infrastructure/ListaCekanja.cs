using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Lista čekanja sa sajta, samo za čitanje.
///
/// Sajt (usluzionica-site, PrijavaServis) svaku prijavu dopisuje kao jedan red
/// JSON-a u /data/prijave.jsonl na svom volumenu. API montira ISTI volumen samo
/// za čitanje (docker-compose.prod.yml) i odavde ga čita. Nema uvoza, nema
/// ručnog koraka i nema druge kopije liste: prijava koja stigne na sajt danas
/// važi za bonus čim je fajl na disku.
///
/// Iz reda se čitaju samo `kontakt` i `kreirano`. Sve ostalo (sekcija sajta,
/// kanal, heš IP-ja) API-ju ne treba.
///
/// Fajl se parsira ponovo samo kad mu se promeni veličina ili vreme izmene —
/// sajt samo dopisuje, pa je to pouzdan znak da je stiglo nešto novo.
/// </summary>
public sealed class ListaCekanja(IConfiguration config, ILogger<ListaCekanja> logger)
{
    private readonly object _brava = new();
    private (DateTime Izmenjen, long Velicina) _verzija;
    private IReadOnlyDictionary<string, DateTime> _prijave = new Dictionary<string, DateTime>();

    /// <summary>Putanja do prijave.jsonl, ili null kad bonus nije uključen.</summary>
    public string? Putanja => config["Waitlist:Path"] is { Length: > 0 } p ? p : null;

    /// <summary>
    /// Adresa (mala slova) → najranije vreme upisa (UTC). Prazno ako fajla nema.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> Prijave()
    {
        var putanja = Putanja;
        if (putanja is null) return new Dictionary<string, DateTime>();

        FileInfo info;
        try
        {
            info = new FileInfo(putanja);
            if (!info.Exists) return new Dictionary<string, DateTime>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lista čekanja nije dostupna.");
            return new Dictionary<string, DateTime>();
        }

        var verzija = (info.LastWriteTimeUtc, info.Length);
        lock (_brava)
        {
            if (verzija == _verzija) return _prijave;
        }

        var nove = Ucitaj(putanja);

        lock (_brava)
        {
            _verzija = verzija;
            _prijave = nove;
            return _prijave;
        }
    }

    /// <summary>Vreme upisa za adresu, ili null ako je nema na listi.</summary>
    public DateTime? Nadji(string? email)
    {
        var kljuc = Normalizuj(email);
        return kljuc is not null && Prijave().TryGetValue(kljuc, out var kada) ? kada : null;
    }

    public static string? Normalizuj(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>SHA-256 normalizovane adrese — vidi WaitlistBonus.</summary>
    public static string Hes(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalizuj(email) ?? ""))).ToLowerInvariant();

    private Dictionary<string, DateTime> Ucitaj(string putanja)
    {
        var rezultat = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var losih = 0;

        // FileShare.ReadWrite: sajt može baš tada da dopisuje red. Poslednji
        // red koji je upravo u pisanju se ne parsira — biće tu pri sledećem čitanju.
        using var tok = new FileStream(putanja, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var citac = new StreamReader(tok, Encoding.UTF8);

        while (citac.ReadLine() is { } red)
        {
            if (string.IsNullOrWhiteSpace(red)) continue;
            try
            {
                using var doc = JsonDocument.Parse(red);
                var koren = doc.RootElement;

                var email = Normalizuj(koren.TryGetProperty("kontakt", out var k) ? k.GetString() : null);
                if (email is null) { losih++; continue; }

                if (!koren.TryGetProperty("kreirano", out var kr) ||
                    !DateTimeOffset.TryParse(kr.GetString(), CultureInfo.InvariantCulture,
                                             DateTimeStyles.AssumeUniversal, out var kada))
                {
                    losih++;
                    continue;
                }

                var utc = kada.UtcDateTime;

                // Sajt odbija duplikate, ali ako ih ipak ima, važi PRVI upis —
                // on odlučuje da li je čovek bio na listi pre izlaska.
                if (!rezultat.TryGetValue(email, out var postojeci) || utc < postojeci)
                    rezultat[email] = utc;
            }
            catch (JsonException)
            {
                losih++;
            }
        }

        if (losih > 0)
            logger.LogWarning("Lista čekanja: {Broj} neispravnih redova preskočeno.", losih);

        return rezultat;
    }
}
