using System.Security.Cryptography;
using System.Text;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Šifrovanje teksta poruka u bazi — AES-256-GCM, sa verzijom ključa.
///
/// Ovo je šifrovanje U MIROVANJU, ne end-to-end: štiti sadržaj ako neko dobije
/// direktan pristup bazi (ukraden backup, procureo connection string). Server i
/// dalje čita poruke — pravi pregled u obaveštenjima i sprovodi rok čuvanja.
///
/// ZAŠTO GCM, A NE CBC KAO RANIJE
///
/// CBC bez MAC-a garantuje tajnost, ali ne i da tekst nije IZMENJEN. Ko ima
/// pristup bazi mogao je da menja šifrovane bajtove i dobija predvidive izmene
/// u dešifrovanom tekstu, a različite greške pri dešifrovanju mogu da posluže
/// kao padding oracle. GCM uz šifrat čuva autentifikacioni tag: bilo kakva
/// izmena obara dešifrovanje.
///
/// FORMAT U BAZI
///
///     v2.{idKljuča}.{Base64( nonce[12] + šifrat + tag[16] )}
///
///   • nonce — 12 nasumičnih bajtova po poruci. Sa nasumičnim nonce-om GCM je
///     bezbedan do ~2³² poruka po ključu; rotacija ključa to resetuje.
///   • AAD = id razgovora. Šifrat premešten u drugi razgovor ne prolazi proveru
///     — sa pristupom bazi se poruka ne može „preneti" iz jednog razgovora u drugi.
///   • idKljuča — omogućava rotaciju bez gubitka: novi ključ šifruje nove poruke,
///     stari ostaje samo za čitanje.
///
/// Zapis bez prefiksa je stara CBC poruka i čita se starim kodom. Poruke se brišu
/// posle 14 dana (MessageCleanupService), pa CBC čitanje sme da se ukloni dve
/// nedelje posle deploya — kad `Text NOT LIKE 'v2.%'` vrati nulu.
///
/// KLJUČEVI
///
///   • k0 — IZVEDEN iz postojećeg Encryption:MessageKey preko HKDF-a, sa
///     sopstvenom namenom. Kriptografski je nezavisan od CBC ključa, a deploy ne
///     traži novu tajnu na serveru — zaboravljen red u .env bi inače oborio
///     server pri prvoj poruci.
///   • k1, k2… — pravi novi ključevi iz Encryption:Keys:{id} za rotaciju;
///     Encryption:CurrentKeyId bira kojim se šifruje. Stari ostaju za čitanje.
/// </summary>
public sealed class MessageEncryption
{
    public const string Prefiks    = "v2.";
    public const string Necitljiva = "[poruka nije čitljiva]";

    private const int    NonceBajtova = 12;
    private const int    TagBajtova   = 16;
    private const string IzvedeniId   = "k0";

    /// <summary>Stari AES-CBC ključ — samo za čitanje poruka iz vremena pre GCM-a.</summary>
    private readonly byte[] _cbcKljuc;

    private readonly Dictionary<string, byte[]> _kljucevi = new(StringComparer.Ordinal);
    private readonly string _tekuciId;

    public MessageEncryption(IConfiguration config)
    {
        _cbcKljuc = Ucitaj(config["Encryption:MessageKey"]
            ?? throw new InvalidOperationException("Encryption:MessageKey nije konfigurisan u appsettings."),
            "Encryption:MessageKey");

        _kljucevi[IzvedeniId] = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, _cbcKljuc, outputLength: 32,
            info: "usluzionica/poruke/aes-gcm/k0"u8.ToArray());

        foreach (var kljuc in config.GetSection("Encryption:Keys").GetChildren())
        {
            // Prazan = slot nije u upotrebi. docker-compose.prod.yml unapred
            // mapira `Encryption__Keys__k1: ${ENCRYPTION_KEY_K1:-}`, pa je
            // rotacija samo dopuna .env-a — dok red u .env-u ne postoji,
            // vrednost je prazna i ne sme da obori start.
            if (string.IsNullOrWhiteSpace(kljuc.Value))
                continue;

            if (kljuc.Key == IzvedeniId || !JeIspravanId(kljuc.Key))
                throw new InvalidOperationException(
                    $"Encryption:Keys:{kljuc.Key} — id ključa mora biti oblika k1, k2… (k0 je rezervisan za izvedeni ključ).");

            _kljucevi[kljuc.Key] = Ucitaj(kljuc.Value, $"Encryption:Keys:{kljuc.Key}");
        }

        _tekuciId = config["Encryption:CurrentKeyId"] is { Length: > 0 } id ? id : IzvedeniId;

        if (!_kljucevi.ContainsKey(_tekuciId))
            throw new InvalidOperationException(
                $"Encryption:CurrentKeyId je '{_tekuciId}', a Encryption:Keys:{_tekuciId} ne postoji.");
    }

    /// <summary>
    /// Šifruje tekst poruke za zadati razgovor. Isti tekst svaki put daje
    /// drugačiji rezultat (nov nonce).
    /// </summary>
    public string Encrypt(string plaintext, int conversationId)
    {
        var tekst = Encoding.UTF8.GetBytes(plaintext);
        var izlaz = new byte[NonceBajtova + tekst.Length + TagBajtova];

        var nonce  = izlaz.AsSpan(0, NonceBajtova);
        var sifrat = izlaz.AsSpan(NonceBajtova, tekst.Length);
        var tag    = izlaz.AsSpan(NonceBajtova + tekst.Length, TagBajtova);

        RandomNumberGenerator.Fill(nonce);

        using var gcm = new AesGcm(_kljucevi[_tekuciId], TagBajtova);
        gcm.Encrypt(nonce, tekst, sifrat, tag, Aad(conversationId));

        return $"{Prefiks}{_tekuciId}.{Convert.ToBase64String(izlaz)}";
    }

    /// <summary>
    /// Dešifruje poruku. Baca <see cref="CryptographicException"/> ako je
    /// zapis izmenjen, iz drugog razgovora ili šifrovan nepoznatim ključem.
    /// </summary>
    public string Decrypt(string sacuvano, int conversationId)
    {
        if (!sacuvano.StartsWith(Prefiks, StringComparison.Ordinal))
            return DecryptCbc(sacuvano);

        var ostatak = sacuvano.AsSpan(Prefiks.Length);
        var tacka   = ostatak.IndexOf('.');
        if (tacka <= 0)
            throw new CryptographicException("Neispravan format šifrovane poruke.");

        var id = ostatak[..tacka].ToString();
        if (!_kljucevi.TryGetValue(id, out var kljuc))
            throw new CryptographicException($"Poruka je šifrovana nepoznatim ključem '{id}'.");

        var podaci = Convert.FromBase64String(ostatak[(tacka + 1)..].ToString());
        if (podaci.Length < NonceBajtova + TagBajtova)
            throw new CryptographicException("Šifrovana poruka je prekratka.");

        var duzina = podaci.Length - NonceBajtova - TagBajtova;
        var tekst  = new byte[duzina];

        using var gcm = new AesGcm(kljuc, TagBajtova);
        gcm.Decrypt(
            podaci.AsSpan(0, NonceBajtova),
            podaci.AsSpan(NonceBajtova, duzina),
            podaci.AsSpan(NonceBajtova + duzina, TagBajtova),
            tekst,
            Aad(conversationId));

        return Encoding.UTF8.GetString(tekst);
    }

    /// <summary>
    /// Kao <see cref="Decrypt"/>, ali umesto izuzetka vraća
    /// <see cref="Necitljiva"/>: jedna oštećena poruka ne sme da obori ceo
    /// ekran razgovora.
    /// </summary>
    public string SafeDecrypt(string sacuvano, int conversationId)
    {
        try
        {
            return Decrypt(sacuvano, conversationId);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return Necitljiva;
        }
    }

    // ── Pomoćno ────────────────────────────────────────────────────────────

    /// <summary>Stara AES-256-CBC poruka: Base64(IV[16] + šifrat). Samo čitanje.</summary>
    private string DecryptCbc(string base64)
    {
        var podaci = Convert.FromBase64String(base64);
        if (podaci.Length < 32 || podaci.Length % 16 != 0)
            throw new CryptographicException("Neispravna stara (CBC) poruka.");

        using var aes = Aes.Create();
        aes.Key = _cbcKljuc;

        return Encoding.UTF8.GetString(aes.DecryptCbc(
            podaci.AsSpan(16), podaci.AsSpan(0, 16), PaddingMode.PKCS7));
    }

    /// <summary>Dodatni autentifikovani podaci: poruka pripada TAČNO ovom razgovoru.</summary>
    private static byte[] Aad(int conversationId) =>
        Encoding.UTF8.GetBytes($"usluzionica/razgovor/{conversationId}");

    private static bool JeIspravanId(string id) =>
        id.Length is >= 2 and <= 4 && id[0] == 'k' && id[1..].All(char.IsAsciiDigit);

    private static byte[] Ucitaj(string? base64, string ime)
    {
        byte[] kljuc;
        try
        {
            kljuc = Convert.FromBase64String(base64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{ime} mora biti validan Base64 string.");
        }

        if (kljuc.Length != 32)
            throw new InvalidOperationException(
                $"{ime} mora biti tačno 32 bajta (AES-256). Trenutno: {kljuc.Length} bajta.");

        return kljuc;
    }
}
