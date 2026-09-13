using UsluzionicaServer.Infrastructure.Media;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Zamena za pravu proveru slika u testovima.
///
/// Presuda se postavlja spolja, pa test može da odigra sva tri ishoda bez
/// pravih slika i bez poziva Cloud Vision-a. Isti obrazac kao
/// <see cref="FakeEmailService"/>: singleton koji test čita i podešava.
///
/// Alternativa bi bila da se pravi drugi host preko WithWebHostBuilder samo
/// zbog ove zamene — to bi ponovo pokrenulo migracije i seed nad istom bazom
/// pri svakom test fajlu, bez ikakve koristi.
/// </summary>
public sealed class StubImageModerator : IImageModerator
{
    /// <summary>Šta sledeći poziv vraća. Podrazumevano propušta.</summary>
    public ImageVerdict Verdict { get; set; } = ImageVerdict.Allow;

    /// <summary>Koliko puta je pozvan — dokazuje da se provera uopšte dešava.</summary>
    public int BrojPoziva { get; private set; }

    /// <summary>
    /// Ako je postavljeno, <see cref="ScanAsync"/> baca ovaj izuzetak.
    ///
    /// Postoji da bi se proverilo fail-open ponašanje: pad provere ne sme da
    /// obori upload.
    /// </summary>
    public Exception? BaciGresku { get; set; }

    public void Reset()
    {
        Verdict    = ImageVerdict.Allow;
        BrojPoziva = 0;
        BaciGresku = null;
    }

    public Task<(ImageVerdict, string?)> ScanAsync(Stream image, CancellationToken ct = default)
    {
        BrojPoziva++;

        if (BaciGresku is not null)
            throw BaciGresku;

        return Task.FromResult<(ImageVerdict, string?)>(
            (Verdict, Verdict == ImageVerdict.Allow ? null : "stub: adult=LIKELY"));
    }
}
