using System.ComponentModel.DataAnnotations;
using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.DTOs.Moderation;

// ── BLOKIRANJE ─────────────────────────────────────────────────────────────

/// <summary>Jedan red na ekranu „Blokirani korisnici".</summary>
public sealed class BlockedUserDto
{
    public string    UserId          { get; set; } = string.Empty;
    public string    FullName        { get; set; } = string.Empty;
    public string?   ProfileImageUrl { get; set; }
    public DateTime  BlockedAt       { get; set; }
}

// ── PRIJAVE ────────────────────────────────────────────────────────────────

/// <summary>
/// Telo zahteva za prijavu.
///
/// Meta se šalje kao jedno od dva polja, isto kao što je i u bazi. Validacija
/// da je popunjeno tačno jedno radi se u servisu, jer DataAnnotations ne ume
/// da izrazi „tačno jedno od".
/// </summary>
public sealed class CreateReportDto
{
    [Required]
    public ReportTargetType TargetType { get; set; }

    public int?    ListingId      { get; set; }
    public string? ReportedUserId { get; set; }

    [Required]
    public ReportReason Reason { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }
}

/// <summary>
/// Jedna stavka u redu za admina — JEDNA META, sa svim prijavama sabranim.
///
/// Admin ne pregleda prijave nego mete. Deset prijava istog oglasa je jedan
/// posao, ne deset, i rešava se jednom odlukom.
/// </summary>
public sealed class ReportQueueItemDto
{
    public ReportTargetType TargetType { get; set; }

    /// <summary>Popunjeno kad je meta oglas.</summary>
    public int?    ListingId       { get; set; }
    /// <summary>Popunjeno kad je meta korisnik.</summary>
    public string? ReportedUserId  { get; set; }

    /// <summary>Naslov oglasa ili ime korisnika — da admin zna šta gleda.</summary>
    public string  TargetNaziv     { get; set; } = string.Empty;

    /// <summary>Vlasnik oglasa. Kod prijave korisnika isto što i meta.</summary>
    public string  VlasnikId       { get; set; } = string.Empty;
    public string  VlasnikIme      { get; set; } = string.Empty;

    /// <summary>Po ovome se red sortira.</summary>
    public int     BrojPrijava     { get; set; }

    /// <summary>Kad je stigla prva prijava — razrešava jednak broj prijava.</summary>
    public DateTime NajstarijaPrijava { get; set; }

    /// <summary>Različiti razlozi koje su prijavioci naveli.</summary>
    public List<string> Razlozi { get; set; } = [];

    /// <summary>Samo za oglase. Cleared znači da je već jednom oslobođen.</summary>
    public string? ModerationState { get; set; }

    /// <summary>Da li je meta već uklonjena/deaktivirana.</summary>
    public bool    VecObradjeno    { get; set; }
}

/// <summary>Pojedinačna prijava, kad admin razvije stavku iz reda.</summary>
public sealed class ReportDetailDto
{
    public int      Id           { get; set; }
    public string   ReporterIme  { get; set; } = string.Empty;
    public string   Razlog       { get; set; } = string.Empty;
    public string?  Napomena     { get; set; }
    public string   Status       { get; set; } = string.Empty;
    public DateTime CreatedAt    { get; set; }
    public string?  ResolvedBy   { get; set; }
    public DateTime? ResolvedAt  { get; set; }
}

/// <summary>Šta admin radi sa prijavljenom metom.</summary>
public enum ReportAction
{
    /// <summary>Prijava je osnovana — oglas se arhivira i obeležava kao Removed.</summary>
    UkloniOglas,

    /// <summary>Prijava je osnovana i teška — nalog se deaktivira sa svim oglasima.</summary>
    DeaktivirajNalog,

    /// <summary>Nema prekršaja. Prijave se zatvaraju, sadržaj ostaje.</summary>
    Odbij
}

public sealed class ResolveReportDto
{
    [Required]
    public ReportTargetType TargetType { get; set; }

    public int?    ListingId      { get; set; }
    public string? ReportedUserId { get; set; }

    [Required]
    public ReportAction Action { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }
}
