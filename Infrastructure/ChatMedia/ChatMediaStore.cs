using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UsluzionicaServer.Infrastructure.ChatMedia;

/// <summary>
/// Slike u četu na disku: šifrovanje, putanje, potpisan URL.
///
/// ZAŠTO NE KAO SLIKE OGLASA
///
/// Slike oglasa su javne (wwwroot/uploads, statični fajl za svakoga ko zna
/// URL). Sliku iz četa sme da vidi samo dvoje učesnika, pa leži VAN wwwroot-a
/// (ChatMedia:Path, poseban volumen), ŠIFROVANA, i dohvata se samo potpisanim
/// URL-om koji izdaje server.
///
/// ŠIFROVANJE — AES-256-GCM, ključ izveden iz ključa poruka sa namenom
/// „chat-media" (<see cref="MessageEncryption.IzvedeniKljuc"/>): nezavisan od
/// ključa teksta, ista rotacija, bez nove tajne u .env-u. AAD = id razgovora +
/// id priloga — fajl premešten u drugi razgovor ili zamenjen drugim prilogom ne
/// dešifruje se. Štiti ukraden volumen/backup bez .env-a; nije end-to-end.
///
///     fajl = 'C' 'M' 1 | len(idKljuča) | idKljuča | nonce[12] | šifrat | tag[16]
///
/// POTPISAN URL — img u WebView-u ne šalje Authorization header, pa:
///
///     /api/chat-media/{id}?u={userId}&amp;exp={unix}&amp;sig={HMAC-SHA256}
///
/// exp se zaokružuje na pun sat (+1 h): URL je isti ceo sat, pa ga WebView
/// kešira umesto da ga preuzima pri svakom otvaranju razgovora.
/// </summary>
public sealed class ChatMediaStore
{
    const int NonceBajtova = 12, TagBajtova = 16;
    static readonly byte[] Magija = "CM"u8.ToArray();

    readonly MessageEncryption _kljucevi;
    readonly byte[] _kljucPotpisa;
    readonly ILogger<ChatMediaStore> _logger;

    public string Koren        { get; }
    public long   NajviseBajtova { get; }
    public int    UrlTrajeMinuta { get; }

    public ChatMediaStore(IConfiguration config, IWebHostEnvironment env, MessageEncryption kljucevi, ILogger<ChatMediaStore> logger)
    {
        _kljucevi = kljucevi;
        _logger   = logger;

        // Podrazumevano pored aplikacije, VAN wwwroot-a. U kontejneru je
        // /app/chat-media (volumen chat-media, vidi docker-compose.yml).
        Koren          = Path.GetFullPath(config["ChatMedia:Path"] is { Length: > 0 } p ? p : Path.Combine(env.ContentRootPath, "chat-media"));
        NajviseBajtova = config.GetValue("ChatMedia:MaxBytes", 5L * 1024 * 1024);
        UrlTrajeMinuta = config.GetValue("ChatMedia:UrlTtlMinutes", 60);

        // Ključ potpisa je uvek iz k0: URL živi sat vremena, rotacija ga ne tiče.
        _kljucPotpisa = kljucevi.IzvedeniKljuc("k0", "chat-media-url");
    }

    // ── Disk ───────────────────────────────────────────────────────────────

    /// <summary>„yyyy/MM/dd/{id}.bin" — fascikle po danu, da čistač siročića gleda samo stare dane.</summary>
    public static string RelativnaPutanja(Guid id, DateTime kada) =>
        $"{kada:yyyy}/{kada:MM}/{kada:dd}/{id:N}.bin";

    string Puna(string relativna)
    {
        var puna = Path.GetFullPath(Path.Combine(Koren, relativna));
        if (!puna.StartsWith(Koren, StringComparison.Ordinal))
            throw new InvalidOperationException("Putanja priloga izlazi van ChatMedia:Path.");
        return puna;
    }

    public async Task UpisiAsync(string relativna, byte[] slika, int razgovorId, Guid prilogId, CancellationToken ct = default)
    {
        var puna = Puna(relativna);
        Directory.CreateDirectory(Path.GetDirectoryName(puna)!);
        await File.WriteAllBytesAsync(puna, Sifruj(slika, razgovorId, prilogId), ct);
    }

    public async Task<byte[]?> ProcitajAsync(string relativna, int razgovorId, Guid prilogId, CancellationToken ct = default)
    {
        var puna = Puna(relativna);
        if (!File.Exists(puna)) return null;
        try
        {
            return Desifruj(await File.ReadAllBytesAsync(puna, ct), razgovorId, prilogId);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Prilog {Id} ne može da se dešifruje.", prilogId);
            return null;
        }
    }

    /// <summary>Briše fajl; greška se loguje, a čistač siročića ga kasnije pokupi.</summary>
    public void Obrisi(string relativna)
    {
        try
        {
            var puna = Puna(relativna);
            if (File.Exists(puna)) File.Delete(puna);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Brisanje priloga {Putanja} nije uspelo.", relativna);
        }
    }

    // ── Šifrovanje ─────────────────────────────────────────────────────────

    byte[] Sifruj(byte[] slika, int razgovorId, Guid prilogId)
    {
        var id     = Encoding.ASCII.GetBytes(_kljucevi.TekuciKljucId);
        var zag    = Magija.Length + 2 + id.Length;
        var izlaz  = new byte[zag + NonceBajtova + slika.Length + TagBajtova];

        Magija.CopyTo(izlaz, 0);
        izlaz[2] = 1;
        izlaz[3] = (byte)id.Length;
        id.CopyTo(izlaz, 4);

        var nonce  = izlaz.AsSpan(zag, NonceBajtova);
        var sifrat = izlaz.AsSpan(zag + NonceBajtova, slika.Length);
        var tag    = izlaz.AsSpan(zag + NonceBajtova + slika.Length, TagBajtova);
        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(_kljucevi.IzvedeniKljuc(_kljucevi.TekuciKljucId, "chat-media"), TagBajtova);
        gcm.Encrypt(nonce, slika, sifrat, tag, Aad(razgovorId, prilogId));
        return izlaz;
    }

    byte[] Desifruj(byte[] podaci, int razgovorId, Guid prilogId)
    {
        if (podaci.Length < 4 || podaci[0] != Magija[0] || podaci[1] != Magija[1] || podaci[2] != 1)
            throw new CryptographicException("Nepoznat format priloga.");

        var idDuzina = podaci[3];
        var zag      = 4 + idDuzina;
        if (podaci.Length < zag + NonceBajtova + TagBajtova)
            throw new CryptographicException("Prilog je prekratak.");

        var id = Encoding.ASCII.GetString(podaci, 4, idDuzina);
        if (!_kljucevi.ImaKljuc(id))
            throw new CryptographicException($"Prilog je šifrovan nepoznatim ključem '{id}'.");

        var duzina = podaci.Length - zag - NonceBajtova - TagBajtova;
        var slika  = new byte[duzina];
        using var gcm = new AesGcm(_kljucevi.IzvedeniKljuc(id, "chat-media"), TagBajtova);
        gcm.Decrypt(podaci.AsSpan(zag, NonceBajtova), podaci.AsSpan(zag + NonceBajtova, duzina),
            podaci.AsSpan(zag + NonceBajtova + duzina, TagBajtova), slika, Aad(razgovorId, prilogId));
        return slika;
    }

    static byte[] Aad(int razgovorId, Guid prilogId) =>
        Encoding.ASCII.GetBytes($"{razgovorId}:{prilogId:N}");

    // ── Potpisan URL ───────────────────────────────────────────────────────

    /// <summary>Relativan URL (server ga u odgovoru pretvara u pun — vidi MediaUrlJsonModifier).</summary>
    public string PotpisanUrl(Guid prilogId, string userId, DateTime? sada = null)
    {
        var t   = sada ?? DateTime.UtcNow;
        var sat = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
        var exp = new DateTimeOffset(sat.AddMinutes(UrlTrajeMinuta).AddHours(1)).ToUnixTimeSeconds();
        return $"/api/chat-media/{prilogId:N}?u={Uri.EscapeDataString(userId)}&exp={exp}&sig={Potpis(prilogId, userId, exp)}";
    }

    /// <summary>Potpis odgovara i rok nije prošao. Da je korisnik i dalje učesnik proverava pozivalac.</summary>
    public bool PotpisVazi(Guid prilogId, string userId, long exp, string sig, DateTime? sada = null)
    {
        if (DateTimeOffset.FromUnixTimeSeconds(exp) < (sada ?? DateTime.UtcNow)) return false;
        var ocekivano = Encoding.ASCII.GetBytes(Potpis(prilogId, userId, exp));
        var dobijeno  = Encoding.ASCII.GetBytes(sig ?? "");
        return CryptographicOperations.FixedTimeEquals(ocekivano, dobijeno);
    }

    string Potpis(Guid prilogId, string userId, long exp)
    {
        var poruka = Encoding.UTF8.GetBytes($"{prilogId:N}|{userId}|{exp.ToString(CultureInfo.InvariantCulture)}");
        return Base64UrlEncode(HMACSHA256.HashData(_kljucPotpisa, poruka));
    }

    static string Base64UrlEncode(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
