namespace UsluzionicaServer.Infrastructure.Media;

/// <summary>
/// Provera otpremljenih slika po SADRŽAJU fajla, i dodela ekstenzije na osnovu
/// onoga što je stvarno pronađeno.
///
/// Ranije se radilo dvoje, i oboje je bilo pogrešno:
///
/// 1. Format se proveravao kroz <c>file.ContentType</c> — a to je header koji
///    klijent piše kako hoće. Nije bila kontrola nego uljudna molba.
///
/// 2. Ekstenzija se uzimala iz <c>file.FileName</c>, dakle isto iz korisničkog
///    unosa, i lepila na ime fajla koji završava u <c>wwwroot</c>. Pošto
///    <c>wwwroot</c> servira statični middleware, upload sa imenom
///    <c>payload.html</c> i header-om <c>image/jpeg</c> je pravio fajl koji se
///    servira kao <c>text/html</c> — dakle stored XSS na domenu API-ja. Isto sa
///    <c>.svg</c>, koji sme da nosi <c>&lt;script&gt;</c>.
///
/// Ovde se ekstenzija NIKAD ne uzima od klijenta. Čita se prvih nekoliko bajtova,
/// utvrdi format, i vrati se ekstenzija koju MI biramo. Ime fajla koje je klijent
/// poslao se ne koristi ni za šta.
///
/// Uz to se sadržaj pretražuje za tragovima skripte (vidi
/// <see cref="ContainsScriptMarkerAsync"/>), jer validan JPEG može u sebi nositi
/// i drugi payload — klasično je dopisati ga IZA završnog markera slike, gde ga
/// dekoder ignoriše a drugi parser ne mora.
///
/// Ni to nije garancija: obfuskacija i kodiranje zaobilaze svaku pretragu po
/// obrascu. Jedina potpuna mera je re-enkodiranje slike, koje uništava sve što
/// nije piksel. Zato klijent svaku sliku pretvara u JPEG pre slanja
/// (<c>UsluzionicaApp/Services/ImageConverter.cs</c>), a ovo je odbrana za
/// slučaj da neko zaobiđe klijenta i gađa API direktno.
/// </summary>
public static class ImageUploads
{
    /// <summary>Najveća dozvoljena veličina slike oglasa i cover slike.</summary>
    public const long MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>Najveća dozvoljena veličina avatara.</summary>
    public const long MaxAvatarBytes = 5 * 1024 * 1024;

    /// <summary>Koliko bajtova zaglavlja je dovoljno za sva tri formata.
    /// WebP traži najviše: "RIFF" (0–3), veličina (4–7), "WEBP" (8–11).</summary>
    private const int HeaderBytes = 12;

    private const string UnsupportedMessage = "Dozvoljeni formati: JPEG, PNG, WebP.";

    /// <summary>
    /// Obrasci koji u binarnoj slici nemaju šta da traže. Sve je malim slovima —
    /// pretraga spušta ASCII slova pre poređenja, pa je neosetljiva na veličinu.
    ///
    /// Verovatnoća da se sedmobajtni niz kao "&lt;script" slučajno pojavi u
    /// pikselima je zanemarljiva, pa lažni pozitivi nisu praktičan problem.
    /// </summary>
    private static readonly byte[][] ScriptMarkers =
    [
        "<script"u8.ToArray(),
        "<?php"u8.ToArray(),
        "<?="u8.ToArray(),
        "<%"u8.ToArray(),
        "<html"u8.ToArray(),
        "<!doctype"u8.ToArray(),
        "<svg"u8.ToArray(),
        "<iframe"u8.ToArray(),
        "javascript:"u8.ToArray()
    ];

    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> PngMagic  => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> RiffMagic => "RIFF"u8;
    private static ReadOnlySpan<byte> WebpMagic => "WEBP"u8;

    /// <summary>
    /// Vraća ekstenziju koju treba upisati (uključujući tačku), ili poruku o
    /// grešci. Tačno jedno od to dvoje je različito od <c>null</c>.
    /// </summary>
    public static async Task<(string? Extension, string? Error)> ValidateAsync(
        IFormFile file, long maxBytes, CancellationToken ct = default)
    {
        if (file.Length == 0)
            return (null, "Fajl je prazan.");

        if (file.Length > maxBytes)
            return (null, $"Slika ne sme biti veća od {maxBytes / (1024 * 1024)} MB.");

        // Jedan prolaz kroz sadržaj: zaglavlje za prepoznavanje formata, pa
        // ostatak za pretragu. `CopyToAsync` koji pozivalac zove kasnije otvara
        // svoj stream, pa ovo čitanje ne troši sadržaj.
        await using var stream = file.OpenReadStream();

        var header = new byte[HeaderBytes];
        var read   = await stream.ReadAtLeastAsync(
            header, HeaderBytes, throwOnEndOfStream: false, ct);

        // Fajl kraći od zaglavlja ne može biti nijedan od tri formata.
        if (read < HeaderBytes)
            return (null, UnsupportedMessage);

        var extension = DetectExtension(header);

        if (extension is null)
            return (null, UnsupportedMessage);

        if (await ContainsScriptMarkerAsync(stream, header, ct))
            return (null, "Slika sadrži nedozvoljen sadržaj.");

        return (extension, null);
    }

    /// <summary>
    /// Traži tragove skripte bilo gde u fajlu.
    ///
    /// Zaglavlje može biti besprekoran JPEG, a payload dopisan IZA završnog
    /// markera — dekoder to preskoči, ali parser koji fajl protumači kao HTML
    /// ili PHP ne mora. Zato se pretražuje ceo sadržaj, ne samo početak.
    ///
    /// Čita se u komadima sa preklapanjem, da marker koji padne preko granice
    /// dva komada ne promakne.
    ///
    /// Heuristika, ne dokaz: hvata naivne polyglot fajlove, ne hvata
    /// obfuskaciju. Prava mera je re-enkodiranje slike.
    /// </summary>
    private static async Task<bool> ContainsScriptMarkerAsync(
        Stream stream, byte[] alreadyRead, CancellationToken ct)
    {
        const int chunkSize = 32 * 1024;

        var overlap = ScriptMarkers.Max(m => m.Length) - 1;
        var buffer  = new byte[chunkSize + overlap];

        // Zaglavlje je već pročitano iz stream-a; ubacuje se na početak da
        // marker koji počinje u njemu a nastavlja se dalje ne promakne.
        var carried = Math.Min(alreadyRead.Length, overlap);
        alreadyRead.AsSpan(alreadyRead.Length - carried, carried)
                   .CopyTo(buffer.AsSpan(0, carried));

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carried, chunkSize), ct);
            if (read == 0)
                return false;

            var filled = carried + read;

            if (ScanForMarkers(buffer, filled))
                return true;

            carried = CarryTail(buffer, filled, overlap);
        }
    }

    /// <summary>Spušta ASCII slova na mala i traži bilo koji marker.</summary>
    private static bool ScanForMarkers(byte[] buffer, int length)
    {
        var span = buffer.AsSpan(0, length);

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is >= (byte)'A' and <= (byte)'Z')
                span[i] += 32;
        }

        foreach (var marker in ScriptMarkers)
        {
            if (span.IndexOf(marker.AsSpan()) >= 0)
                return true;
        }

        return false;
    }

    /// <summary>Prenosi rep bafera na početak, da se marker preko granice uhvati.</summary>
    private static int CarryTail(byte[] buffer, int filled, int overlap)
    {
        var carried = Math.Min(overlap, filled);
        buffer.AsSpan(filled - carried, carried).CopyTo(buffer.AsSpan(0, carried));
        return carried;
    }

    /// <summary>Ekstenzija po potpisu formata, ili <c>null</c> ako nije prepoznat.</summary>
    private static string? DetectExtension(byte[] header)
    {
        var head = header.AsSpan();

        if (head.StartsWith(JpegMagic))
            return ".jpg";

        if (head.StartsWith(PngMagic))
            return ".png";

        // WebP: "RIFF" na 0, pa 4 bajta veličine, pa "WEBP" na 8.
        if (head.StartsWith(RiffMagic) && head[8..12].SequenceEqual(WebpMagic))
            return ".webp";

        return null;
    }
}
