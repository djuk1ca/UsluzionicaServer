using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Prijava oglasa ili korisnika.
///
/// META JE DVA NULLABLE FK-a, NE POLIMORFNI <c>int TargetId</c>.
/// Polimorfni ključ ne može biti strani ključ, pa baza ne bi imala nikakav način
/// da proveri da meta uopšte postoji — a prijava koja pokazuje na obrisan red je
/// gore nego nikakva, jer zauzima mesto u redu za admina i ne može se rešiti.
/// Ovako CHECK ograničenje traži da je popunjeno tačno jedno polje, a FK čuva
/// da ono na šta pokazuje zaista postoji.
///
/// Redovi se NE brišu kad se meta ukloni — to je revizijski trag. Zato su svi
/// FK-ovi <c>Restrict</c>; oglasi se ionako arhiviraju, ne brišu fizički
/// (<c>ListingService.DeleteAsync</c> postavlja <c>Status = Archived</c>).
/// </summary>
public class Report
{
    public int    Id         { get; set; }

    /// <summary>Ko prijavljuje.</summary>
    public string ReporterId { get; set; } = string.Empty;

    public ReportTargetType TargetType { get; set; }

    /// <summary>Popunjeno kad je <see cref="TargetType"/> = Listing.</summary>
    public int?    ListingId      { get; set; }

    /// <summary>Popunjeno kad je <see cref="TargetType"/> = User.</summary>
    public string? ReportedUserId { get; set; }

    public ReportReason Reason { get; set; }

    /// <summary>Slobodna napomena prijavioca. Jedini kontekst kad je razlog „Drugo".</summary>
    public string? Note { get; set; }

    public ReportStatus Status    { get; set; } = ReportStatus.Pending;
    public DateTime     CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Rešavanje ──────────────────────────────────────────────────────────
    public DateTime? ResolvedAt     { get; set; }
    public string?   ResolvedById   { get; set; }

    /// <summary>Beleška admina o odluci. Vidi se samo u admin panelu.</summary>
    public string?   ResolutionNote { get; set; }

    // Navigation
    public ApplicationUser  Reporter     { get; set; } = null!;
    public Listing?         Listing      { get; set; }
    public ApplicationUser? ReportedUser { get; set; }
    public ApplicationUser? ResolvedBy   { get; set; }
}
