using System.ComponentModel.DataAnnotations;
using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.DTOs.Jobs;

// ── Čitanje ────────────────────────────────────────────────────────────────

/// <summary>
/// Posao za karticu i detalj. Isti oblik za sve uloge — polja koja se tiču
/// samo autora (<see cref="NewApplicationsCount"/>, <see cref="Assigned"/>…)
/// ostaju prazna za ostale.
/// </summary>
public sealed class JobDto
{
    public int        Id           { get; set; }
    public string     Title        { get; set; } = string.Empty;
    public string     Description  { get; set; } = string.Empty;
    public int        CategoryId   { get; set; }
    public string     CategoryName { get; set; } = string.Empty;
    public string     CategorySlug { get; set; } = string.Empty;
    public string     Location     { get; set; } = string.Empty;

    /// <summary>null = „Po dogovoru" (ili volonterski).</summary>
    public decimal?   PayAmount    { get; set; }

    /// <summary>Način isplate („80.000 RSD mesečno"). null = volonterski posao.</summary>
    public JobPayType? PayType     { get; set; }

    /// <summary>Volonterski posao — bez isplate, oznaka „Volonterski".</summary>
    public bool       IsVolunteer  { get; set; }

    public JobStatus  Status       { get; set; }
    public DateTime   CreatedAt    { get; set; }
    public DateTime   ExpiresAt    { get; set; }

    /// <summary>Istekao: posle ovoga se trajno briše (7 dana za produženje).</summary>
    public DateTime?  PurgeAt      { get; set; }

    /// <summary>Autor sme da produži (poslednja 3 dana ili istekao).</summary>
    public bool       CanExtend    { get; set; }

    public bool       IsBoosted    { get; set; }
    public DateTime?  BoostExpiresAt { get; set; }

    /// <summary>Dok je posao dogovoren: koliko je isticanja ostalo (pauzirano).</summary>
    public double?    BoostPausedDays { get; set; }

    /// <summary>Broj javljanja koja važe (poslata ili izabrano).</summary>
    public int        ApplicationsCount    { get; set; }

    /// <summary>Samo autoru: javljanja koja još nije video na ekranu upravljanja.</summary>
    public int        NewApplicationsCount { get; set; }

    public List<JobImageDto> Images { get; set; } = [];

    public JobPersonDto Author { get; set; } = new();

    /// <summary>Gledaočevo javljanje na ovaj posao, ako postoji.</summary>
    public MyApplicationDto? MyApplication { get; set; }

    /// <summary>Autoru i izabranom: dogovor (izvršilac, od kada, kada može „izvršen").</summary>
    public AssignmentDto? Assigned { get; set; }

    /// <summary>Koliko još puta autor sme da otkaže izvršioca.</summary>
    public int        CancellationsLeft { get; set; }

    public bool       IsMine       { get; set; }
}

public sealed class JobImageDto
{
    public int    Id        { get; set; }
    public string ImageUrl  { get; set; } = string.Empty;
    public int    SortOrder { get; set; }
}

/// <summary>Osoba uz posao (autor ili izvršilac) — sa ocenom u ulozi koja je bitna.</summary>
public sealed class JobPersonDto
{
    public string   UserId          { get; set; } = string.Empty;
    public string   FullName        { get; set; } = string.Empty;
    public string?  ProfileImageUrl { get; set; }

    /// <summary>Autor: ocena kao naručilac. Izvršilac: ocena kao uslugodavac.</summary>
    public decimal  Rating          { get; set; }
    public int      RatingCount     { get; set; }

    public int?     ProviderProfileId { get; set; }
    public bool     IsVerified      { get; set; }
    public DateTime MemberSince     { get; set; }
}

public sealed class MyApplicationDto
{
    public int                  Id          { get; set; }
    public JobApplicationStatus Status      { get; set; }
    public string               Message     { get; set; } = string.Empty;
    public decimal?             OfferAmount { get; set; }
    public DateTime             CreatedAt   { get; set; }
    public string?              CancelReason { get; set; }

    /// <summary>Posao je dogovoren sa nekim drugim — „Izabran je drugi".</summary>
    public bool                 OtherChosen { get; set; }
}

public sealed class AssignmentDto
{
    public int          ApplicationId { get; set; }
    public JobPersonDto Worker        { get; set; } = new();
    public decimal?     OfferAmount   { get; set; }
    public DateTime     AssignedAt    { get; set; }

    /// <summary>Od kada autor sme da označi „Posao je izvršen".</summary>
    public DateTime     CompletableAt { get; set; }

    public int?         ConversationId { get; set; }
}

/// <summary>Javljanje kako ga vidi autor na ekranu upravljanja.</summary>
public sealed class JobApplicationDto
{
    public int                  Id          { get; set; }
    public JobPersonDto         Provider    { get; set; } = new();
    public string               Message     { get; set; } = string.Empty;
    public decimal?             OfferAmount { get; set; }
    public JobApplicationStatus Status      { get; set; }
    public bool                 IsReapplied { get; set; }
    public DateTime             CreatedAt   { get; set; }
    public int?                 ConversationId { get; set; }
}

/// <summary>Moji poslovi (Objavio sam) — tabovi + traka „Za tebe".</summary>
public sealed class MyJobsDto
{
    public List<JobDto>       Active   { get; set; } = [];
    public List<JobDto>       Assigned { get; set; } = [];
    public List<JobDto>       Expired  { get; set; } = [];

    /// <summary>Uklonjeni od strane moderacije — kartica sa pravilima.</summary>
    public List<JobDto>       Removed  { get; set; } = [];

    public List<ForYouItemDto> ForYou  { get; set; } = [];
}

/// <summary>
/// Kartica „Za tebe": nešto traži pažnju. Vrste: expiring, new-applications,
/// completable, review. Klijent bira ikonu i dugme po vrsti.
/// </summary>
public sealed class ForYouItemDto
{
    public string Kind     { get; set; } = string.Empty;
    public int    JobId    { get; set; }
    public string JobTitle { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Za „review": koga treba oceniti.</summary>
    public string? SubjectUserId { get; set; }
}

/// <summary>Javio sam se — kartica posla sa stanjem mog javljanja.</summary>
public sealed class AppliedJobDto
{
    public JobDto           Job         { get; set; } = new();
    public MyApplicationDto Application { get; set; } = new();
    public int?             ConversationId { get; set; }
}

public sealed class PagedJobs
{
    public List<JobDto> Items    { get; set; } = [];
    public int          Total    { get; set; }
    public int          Page     { get; set; }
    public int          PageSize { get; set; }
}

// ── Pisanje ────────────────────────────────────────────────────────────────

public sealed class JobQueryParams
{
    public string? Q            { get; set; }
    public string? City         { get; set; }
    public string? CategorySlug { get; set; }

    /// <summary>true = samo istaknuti (sekcija), false = bez istaknutih (lista), null = svi.</summary>
    public bool?   Boosted      { get; set; }

    /// <summary>
    /// true = samo volonterski (traka „Volonterske akcije", sopstveni poredak),
    /// false = bez volonterskih (sekcija istaknutih i lista), null = svi.
    /// </summary>
    public bool?   Volunteer    { get; set; }

    public int     Page         { get; set; } = 1;
    public int     PageSize     { get; set; } = 20;

    /// <summary>Postavlja kontroler iz JWT-a — nikad iz upita.</summary>
    public string? ViewerUserId { get; set; }
}

public sealed class CreateJobDto
{
    [Required, MinLength(5, ErrorMessage = "Naslov mora imati bar 5 znakova."), MaxLength(80)]
    public string   Title       { get; set; } = string.Empty;

    [Required]
    public int      CategoryId  { get; set; }

    [Required, MaxLength(200)]
    public string   Location    { get; set; } = string.Empty;

    [Required, MinLength(10, ErrorMessage = "Opiši posao u bar jednoj rečenici."), MaxLength(2000)]
    public string   Description { get; set; } = string.Empty;

    /// <summary>null = „po dogovoru".</summary>
    [Range(1, 10_000_000)]
    public decimal? PayAmount   { get; set; }

    /// <summary>Način isplate. Bez iznosa uvek postaje Negotiable.</summary>
    public JobPayType PayType   { get; set; } = JobPayType.OneTime;

    /// <summary>Volonterski — iznos i način isplate se ignorišu.</summary>
    public bool     IsVolunteer { get; set; }
}

/// <summary>Izmena — dok ima javljanja, menjaju se SAMO opis (slike idu posebno).</summary>
public sealed class UpdateJobDto
{
    [MaxLength(80)]
    public string?  Title       { get; set; }

    public int?     CategoryId  { get; set; }

    [MaxLength(200)]
    public string?  Location    { get; set; }

    [Required, MinLength(10), MaxLength(2000)]
    public string   Description { get; set; } = string.Empty;

    [Range(1, 10_000_000)]
    public decimal? PayAmount   { get; set; }

    /// <summary>true = „po dogovoru" (PayAmount se briše).</summary>
    public bool     PayNegotiable { get; set; }

    /// <summary>null = ne menja se. Negotiable briše iznos kao PayNegotiable.</summary>
    public JobPayType? PayType  { get; set; }

    /// <summary>null = ne menja se. true briše iznos i način isplate.</summary>
    public bool?    IsVolunteer { get; set; }
}

public sealed class ApplyToJobDto
{
    [Required(ErrorMessage = "Napiši kratku poruku — autor bira po njoj."),
     MinLength(2, ErrorMessage = "Napiši kratku poruku — autor bira po njoj."),
     MaxLength(500)]
    public string   Message     { get; set; } = string.Empty;

    /// <summary>Na volonterski posao se ne upisuje — nema isplate.</summary>
    [Range(1, 10_000_000)]
    public decimal? OfferAmount { get; set; }
}

public sealed class AssignJobDto
{
    [Required]
    public int ApplicationId { get; set; }
}

public sealed class UnassignJobDto
{
    /// <summary>Opciono — izvršilac ga vidi.</summary>
    [MaxLength(80)]
    public string? Reason { get; set; }
}

public sealed class BoostJobDto
{
    [Range(0.01, double.MaxValue, ErrorMessage = "Broj tokena mora biti pozitivan.")]
    public decimal TokensToSpend { get; set; }

    public int     DurationDays  { get; set; }
}
