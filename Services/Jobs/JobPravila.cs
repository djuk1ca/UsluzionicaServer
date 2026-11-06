namespace UsluzionicaServer.Services.Jobs;

/// <summary>
/// Brojevi koji upravljaju poslovima, na jednom mestu (appsettings „Jobs:*").
///
/// Čita se jednom — singleton. Podrazumevane vrednosti su odluke iz plana
/// (Obsidian „Plan — Poslovi i ocene korisnika (v2)" §6 i §12), pa server radi
/// ispravno i bez ijednog reda u konfiguraciji.
/// </summary>
public sealed class JobPravila(IConfiguration config)
{
    /// <summary>Autor dobija za izvršen posao.</summary>
    public decimal NagradaAutoru     { get; } = config.GetValue("Jobs:PosterRewardTokens", 3m);

    /// <summary>Izvršilac dobija za izvršen posao.</summary>
    public decimal NagradaIzvrsiocu  { get; } = config.GetValue("Jobs:WorkerRewardTokens", 5m);

    public int     NajviseSlika      { get; } = config.GetValue("Jobs:MaxImages", 2);
    public int     NajviseAktivnih   { get; } = config.GetValue("Jobs:MaxOpenPerUser", 5);
    public int     TrajanjeDana      { get; } = config.GetValue("Jobs:ExpireDays", 30);

    /// <summary>„Produži" je dostupno kad je do isteka ostalo najviše ovoliko dana.</summary>
    public int     ProzorProduzenja  { get; } = config.GetValue("Jobs:ExtendWindowDays", 3);

    /// <summary>Istekao posao čeka na „Produži" ovoliko dana, pa se briše.</summary>
    public int     CekanjeIsteklog   { get; } = config.GetValue("Jobs:ExpiredGraceDays", 7);

    public int     NajviseOtkazivanja { get; } = config.GetValue("Jobs:MaxCancellations", 3);

    /// <summary>Posle izvršenja red ostaje ovoliko (rok za ocene), pa se trajno briše.</summary>
    public int     RokZaOcene        { get; } = config.GetValue("Jobs:ReviewWindowDays", 14);

    /// <summary>
    /// Čekanje od izbora do „Posao je izvršen" — isto kao za usluge
    /// (Booking:ExecuteAfterDays). Brana od lažnih poslova između dva naloga.
    /// </summary>
    public int     CekanjeDoIzvrsenja { get; } = config.GetValue("Booking:ExecuteAfterDays", 7);

    /// <summary>Posle otkazivanja posao se vraća sa najmanje ovoliko dana roka.</summary>
    public TimeSpan NajmanjeRokaPosleOtkazivanja { get; } = TimeSpan.FromDays(3);
}
