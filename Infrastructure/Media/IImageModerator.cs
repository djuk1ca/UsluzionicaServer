namespace UsluzionicaServer.Infrastructure.Media;

/// <summary>Ishod automatske provere slike.</summary>
public enum ImageVerdict
{
    /// <summary>Slika je čista — upload prolazi bez traga.</summary>
    Allow,

    /// <summary>
    /// Sumnjivo, ali ne dovoljno da se odbije. Slika se PRIMA, a automatski se
    /// pravi prijava da je čovek pregleda.
    /// </summary>
    Review,

    /// <summary>Sigurno neprikladno — upload se odbija.</summary>
    Reject
}

/// <summary>
/// Automatska provera otpremljenih slika.
///
/// TRI NIVOA, NE DVA — i to je za Uslužionicu suština, ne detalj.
///
/// U bazi već postoje kategorije „Depilacija voskom", „Depilacija šećerom",
/// „Fitnes trener" i „Plivanje — instruktor". Legitimna fotografija u tim
/// kategorijama redovno sadrži golu kožu: leđa, noge, stomak, osoba u kupaćem
/// na bazenu. Binarni filter „ima kože → odbij" blokirao bi tačne oglase,
/// korisnik ne bi dobio objašnjenje i ne bi znao šta da promeni — dakle
/// napravio bi veći problem nego što rešava.
///
/// Zato srednji nivo: sumnjiva slika prolazi, a prijava ide čoveku.
///
/// FAIL-OPEN. Ako provera ne uspe — mreža, istek vremena, greška servisa —
/// vraća se <see cref="ImageVerdict.Review"/>, nikad <c>Reject</c>. Pad tuđeg
/// servisa ne sme da obori postavljanje oglasa. Isti princip kao
/// <c>AbortOnConnectFail=false</c> kod Redisa.
/// </summary>
public interface IImageModerator
{
    /// <param name="image">
    /// Sadržaj slike. Implementacija je dužna da ga pročita od početka i da ne
    /// ostavi poziciju pomerenu — pozivalac isti stream koristi za upis.
    /// </param>
    Task<(ImageVerdict Verdict, string? Detalji)> ScanAsync(
        Stream image, CancellationToken ct = default);
}

/// <summary>
/// Ne proverava ništa — sve propušta.
///
/// Podrazumevana implementacija: u razvoju i u testovima automatska provera
/// nema šta da radi, a Cloud Vision bi tražio ključ i trošio kvotu na
/// izmišljene slike.
///
/// Bira se konfiguracijom (<c>ImageModeration:Provider</c>), pa je uključivanje
/// prave provere na produkciji samo pitanje podešavanja.
/// </summary>
public sealed class NoopImageModerator : IImageModerator
{
    public Task<(ImageVerdict, string?)> ScanAsync(Stream image, CancellationToken ct = default)
        => Task.FromResult<(ImageVerdict, string?)>((ImageVerdict.Allow, null));
}
