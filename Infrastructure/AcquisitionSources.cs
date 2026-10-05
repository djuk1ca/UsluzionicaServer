namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Odgovori na „Kako si čuo za nas?" pri registraciji.
///
/// Lista prati marketinški plan (Marketing 11 §3.2). U bazu idu KODOVI, a ne
/// tekst sa ekrana: tekst se menja (prevod, preformulacija), a nedeljni upiti
/// nad bazom moraju da broje isto i za stare i za nove naloge.
///
/// Pitanje je opciono. Nepoznata vrednost se ne odbija nego se upisuje kao
/// „nije odgovoreno" — registracija ne sme da padne zbog marketinškog pitanja,
/// a slobodan tekst bi pokvario brojanje po kanalima.
/// </summary>
public static class AcquisitionSources
{
    public static readonly IReadOnlySet<string> Dozvoljeni = new HashSet<string>(StringComparer.Ordinal)
    {
        "tiktok",
        "instagram",
        "facebook",
        "youtube",
        "linkedin",
        "prijatelj",      // Prijatelj ili poznanik
        "uslugodavac",    // Uslugodavac me pozvao
        "plakat",         // Plakat ili flajer
        "prodavnica",     // U prodavnici
        "clanak-tv",      // Članak ili TV
        "google",         // Google pretraga
        "drugo"
    };

    /// <summary>Kod iz liste, ili null ako odgovora nema ili nije prepoznat.</summary>
    public static string? Normalizuj(string? vrednost)
    {
        if (string.IsNullOrWhiteSpace(vrednost)) return null;

        var kod = vrednost.Trim().ToLowerInvariant();
        return Dozvoljeni.Contains(kod) ? kod : null;
    }
}
