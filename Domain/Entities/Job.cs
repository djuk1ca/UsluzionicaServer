using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Posao — obrnut smer od oglasa: korisnik (ili uslugodavac) objavi šta mu
/// treba, uslugodavci se javljaju, autor bira jednog.
///
/// ZASEBAN ENTITET, NE OGLAS SA ZASTAVICOM: drugi autor, druga akcija, drugi
/// životni ciklus. Zastavica bi ušla u svaki upit nad oglasima, boost i keš
/// pretrage. Ceo ciklus i pravila: Obsidian „Plan — Poslovi i ocene
/// korisnika (v2)" §3.
/// </summary>
public class Job
{
    public int       Id          { get; set; }

    /// <summary>Ko je objavio — korisnik ILI uslugodavac.</summary>
    public string    AuthorId    { get; set; } = string.Empty;

    public int       CategoryId  { get; set; }
    public string    Location    { get; set; } = string.Empty;
    public string    Title       { get; set; } = string.Empty;

    /// <summary>Prazno posle izvršenja/brisanja — opis se ne čuva duže nego što treba.</summary>
    public string    Description { get; set; } = string.Empty;

    /// <summary>null = „po dogovoru". Volonterski posao je uvek null.</summary>
    public decimal?  PayAmount   { get; set; }

    /// <summary>
    /// Način isplate — određuje kako se iznos prikazuje („mesečno"). null iznos
    /// je uvek <see cref="JobPayType.Negotiable"/>.
    /// </summary>
    public JobPayType PayType    { get; set; } = JobPayType.OneTime;

    /// <summary>
    /// Volonterski posao — građanske i studentske radne akcije za opšte dobro.
    /// Bez isplate: iznos i tip se ne čuvaju i ne šalju; na pretrazi stoji u
    /// sopstvenoj traci „Volonterske akcije" (vidi JobService.SearchAsync).
    /// </summary>
    public bool      IsVolunteer { get; set; }

    public JobStatus Status      { get; set; } = JobStatus.Open;

    // ── Dogovor ────────────────────────────────────────────────────────────
    public int?      AssignedApplicationId { get; set; }

    /// <summary>Od ovog trenutka teče čekanje do „Posao je izvršen" (Booking:ExecuteAfterDays).</summary>
    public DateTime? AssignedAt  { get; set; }

    /// <summary>Koliko puta je autor otkazao izvršioca (najviše Jobs:MaxCancellations).</summary>
    public int       CancelCount { get; set; }

    /// <summary>Obaveštenje „možeš da označiš izvršen" poslato za trenutni dogovor.</summary>
    public DateTime? CompletableNotifiedAt { get; set; }

    /// <summary>
    /// Kada je autor poslednji put otvorio javljanja. Novija javljanja su
    /// „nova" (plavo na kartici i u traci „Za tebe").
    /// </summary>
    public DateTime? ApplicationsSeenAt { get; set; }

    // ── Rok ────────────────────────────────────────────────────────────────
    public DateTime  ExpiresAt   { get; set; }

    /// <summary>
    /// Preostalo vreme u trenutku izbora. Dok je posao dogovoren, rok stoji —
    /// posle otkazivanja posao se vraća sa vremenom koje je imao (odluka 11).
    /// </summary>
    public TimeSpan? RemainingOnAssign { get; set; }

    /// <summary>Obaveštenje „ističe sutra" poslato za trenutni rok.</summary>
    public DateTime? ExpiryNotifiedAt  { get; set; }

    public DateTime? ExpiredAt   { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Kada JobCleanupService trajno briše red (null = ne briše se).</summary>
    public DateTime? PurgeAt     { get; set; }

    // ── Isticanje (isti mehanizam kao oglasi) ──────────────────────────────
    public bool      IsBoosted              { get; set; }
    public DateTime? BoostExpiresAt         { get; set; }
    public decimal   BoostScore             { get; set; }

    /// <summary>Preostalo isticanje u trenutku izbora — stoji dok je posao dogovoren.</summary>
    public TimeSpan? BoostRemainingOnAssign { get; set; }

    public ModerationState ModerationState { get; set; } = ModerationState.Clean;

    // ── Denormalizovani indeks za pretragu (kao Listing) ───────────────────
    // Održava AppDbContext.SaveChanges kroz SearchIndexer.
    public string SearchTitle    { get; set; } = string.Empty;
    public string SearchLocation { get; set; } = string.Empty;
    public string SearchBody     { get; set; } = string.Empty;
    public int    SearchVersion  { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser              Author       { get; set; } = null!;
    public Category                     Category     { get; set; } = null!;
    public ICollection<JobImage>        Images       { get; set; } = [];
    public ICollection<JobApplication>  Applications { get; set; } = [];
    public ICollection<JobBoost>        Boosts       { get; set; } = [];
}
