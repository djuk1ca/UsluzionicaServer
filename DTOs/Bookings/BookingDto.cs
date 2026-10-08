using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Bookings;

/// <summary>
/// Response DTO — vraća se klijentu i provideru pri svim booking operacijama.
/// </summary>
public sealed class BookingDto
{
    public int     Id             { get; set; }

    public int     ListingId      { get; set; }
    public string  ListingTitle   { get; set; } = string.Empty;

    // Cena oglasa — kartica zahteva kod uslugodavca je prikazuje, da ne mora
    // da otvara oglas da bi znao o čemu je reč.
    /// <summary>Fixed | Range | Negotiable — isto kao na oglasu.</summary>
    public string   PriceMode       { get; set; } = string.Empty;
    public decimal? FixedPrice      { get; set; }
    public decimal? PriceFrom       { get; set; }
    public decimal? PriceTo         { get; set; }

    // RequestedDate/RequestedTime se NAMERNO ne šalju: CreateAsync ih danas
    // puni trenutkom pravljenja (placeholder do zakazivanja termina), pa bi
    // ih aplikacija prikazala kao „termin" koji klijent nikad nije izabrao.

    public string  ClientId       { get; set; } = string.Empty;
    public string  ClientName     { get; set; } = string.Empty;
    public string? ClientImageUrl { get; set; }

    public string  ProviderUserId { get; set; } = string.Empty;
    public string  ProviderName   { get; set; } = string.Empty;

    public string? Notes          { get; set; }

    /// <summary>Pending | Confirmed | Rejected | Completed | Cancelled</summary>
    public string  Status         { get; set; } = string.Empty;

    public DateTime  CreatedAt    { get; set; }

    /// <summary>Postavljeno kad provider potvrdi — osnova za pravilo čekanja.</summary>
    public DateTime? AcceptedAt   { get; set; }

    /// <summary>Koliko dana posle prihvatanja „Izvršeno" postaje dostupno (Booking:ExecuteAfterDays).</summary>
    public int       ExecuteAfterDays { get; set; }

    /// <summary>
    /// Trenutak (UTC) od kog provider može da označi uslugu kao izvršenu —
    /// aplikacija po njemu prikazuje odbrojavanje. Null dok zahtev nije prihvaćen.
    /// </summary>
    public DateTime? CanExecuteAt { get; set; }

    /// <summary>
    /// True ako je booking Confirmed I prošlo je ExecuteAfterDays dana od potvrde.
    /// Provider može pritisnuti Execute samo kad je CanExecute = true.
    /// </summary>
    public bool CanExecute { get; set; }
}

/// <summary>
/// Body za POST /api/bookings — bez datuma/vremena (Business plan feature).
/// </summary>
public sealed class CreateBookingDto
{
    [Required]
    public int ListingId { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}
