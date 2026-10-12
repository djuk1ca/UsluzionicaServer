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
/// Iz reda se čitaju `kontakt`, `kreirano`, `platforma` (uređaj sa kog je
/// prijava poslata) i, ako postoji, `lista`. Ostalo (sekcija sajta, kanal, heš
/// IP-ja) API-ju ne treba. Kojoj listi prijava pripada (Android ili iPhone)
/// odlučuje <see cref="Services.ListaCekanjaBonus"/>, ne čitač.
///
/// Fajl se parsira ponovo samo kad mu se promeni veličina ili vreme izmene —
/// sajt samo dopisuje, pa je to pouzdan znak da je stiglo nešto novo.
/// </summary>
public sealed class ListaCekanja(IConfiguration config, ILogger<ListaCekanja> logger)
{
    /// <summary>Jedna adresa sa liste.</summary>
    /// <param name="Kreirano">Najraniji upis (UTC).</param>
    /// <param name="Platforma">„android", „ios", „desktop" ili null (stari redovi).</param>
    /// <param name="Lista">Izričita lista ako je sajt upiše („android"/„ios"), inače null.</param>
    public sealed record Prijava(DateTime Kreirano, string? Platforma, string? Lista);

    private static readonly IReadOnlyDictionary<string, Prijava> Prazno = new Dictionary<string, Prijava>();

    private readonly object _brava = new();
    private (DateTime Izmenjen, long Velicina) _verzija;
    private IReadOnlyDictionary<string, Prijava> _prijave = Prazno;

    /// <summary>Putanja do prijave.jsonl, ili null kad bonus nije uključen.</summary>
    public string? Putanja => config["Waitlist:Path"] is { Length: > 0 } p ? p : null;

    /// <summary>Adresa (mala slova) → prijava. Prazno ako fajla nema.</summary>
    public IReadOnlyDictionary<string, Prijava> Prijave()
    {
        var putanja = Putanja;
        if (putanja is null) return Prazno;

        FileInfo info;
        try
        {
            info = new FileInfo(putanja);
            if (!info.Exists) return Prazno;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lista čekanja nije dostupna.");
            return Prazno;
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

    /// <summary>Prijava za adresu, ili null ako je nema na listi.</summary>
    public Prijava? Nadji(string? email)
    {
        var kljuc = Normalizuj(email);
        return kljuc is not null && Prijave().TryGetValue(kljuc, out var p) ? p : null;
    }

    public static string? Normalizuj(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>SHA-256 normalizovane adrese — vidi WaitlistBonus.</summary>
    public static string Hes(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalizuj(email) ?? ""))).ToLowerInvariant();

    private Dictionary<string, Prijava> Ucitaj(string putanja)
    {
        var rezultat = new Dictionary<string, Prijava>(StringComparer.Ordinal);
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

                var email = Normalizuj(Tekst(koren, "kontakt"));
                if (email is null) { losih++; continue; }

                if (!DateTimeOffset.TryParse(Tekst(koren, "kreirano"), CultureInfo.InvariantCulture,
                                             DateTimeStyles.AssumeUniversal, out var kada))
                {
                    losih++;
                    continue;
                }

                var prijava = new Prijava(
                    kada.UtcDateTime,
                    Tekst(koren, "platforma")?.Trim().ToLowerInvariant(),
                    Tekst(koren, "lista")?.Trim().ToLowerInvariant());

                // Sajt odbija duplikate, ali ako ih ipak ima, važi PRVI upis —
                // on odlučuje da li je čovek bio na listi pre izlaska.
                if (!rezultat.TryGetValue(email, out var postojeca) || prijava.Kreirano < postojeca.Kreirano)
                    rezultat[email] = prijava;
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

    private static string? Tekst(JsonElement koren, string ime) =>
        koren.TryGetProperty(ime, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
