using System.Text.RegularExpressions;

namespace UsluzionicaServer.Infrastructure.Media;

/// <summary>
/// Slike se u bazi čuvaju kao RELATIVNE putanje ("/uploads/listings/3/abc.jpg").
///
/// Ranije se upisivao pun URL sa domenom, pa bi svaka promena domena
/// (localhost → produkcija, ili kasnije CDN) polomila sve postojeće slike i
/// zahtevala migraciju podataka. Relativna putanja je prenosiva.
///
/// Pun URL se sastavlja tek pri serijalizaciji odgovora — vidi
/// <see cref="MediaUrlJsonModifier"/>.
/// </summary>
public static partial class MediaUrls
{
    /// <summary>Prefiks pod kojim se serviraju sve otpremljene datoteke.</summary>
    public const string UploadsPrefix = "/uploads/";

    [GeneratedRegex(@"^https?://[^/]+", RegexOptions.IgnoreCase)]
    private static partial Regex OriginPrefix();

    /// <summary>
    /// Svodi vrednost na relativnu putanju. Podnosi i pun URL (nasleđeni podaci)
    /// i već relativnu putanju (idempotentno).
    /// </summary>
    public static string? ToRelative(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var trimmed = url.Trim();

        // Skini shemu i host ako ih ima: "https://host/uploads/x" → "/uploads/x"
        var relative = OriginPrefix().Replace(trimmed, string.Empty);

        if (relative.Length == 0) return null;
        return relative.StartsWith('/') ? relative : "/" + relative;
    }

    /// <summary>
    /// Sastavlja pun URL za klijenta. Spoljne URL-ove (npr. avatar sa Google
    /// naloga posle OAuth prijave) prosleđuje netaknute.
    /// </summary>
    public static string? ToAbsolute(string? storedValue, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(storedValue)) return null;

        var value = storedValue.Trim();

        // Već pun URL — ne diramo (spoljni izvori, ili nasleđeni podaci).
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;

        return baseUrl.TrimEnd('/') + (value.StartsWith('/') ? value : "/" + value);
    }
}
