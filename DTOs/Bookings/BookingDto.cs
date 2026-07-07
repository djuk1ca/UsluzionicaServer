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

    public string  ClientId       { get; set; } = string.Empty;
    public string  ClientName     { get; set; } = string.Empty;
    public string? ClientImageUrl { get; set; }

    public string  ProviderUserId { get; set; } = string.Empty;
    public string  ProviderName   { get; set; } = string.Empty;

    public string? Notes          { get; set; }

    /// <summary>Pending | Confirmed | Rejected | Completed | Cancelled</summary>
    public string  Status         { get; set; } = string.Empty;

    public DateTime  CreatedAt    { get; set; }

    /// <summary>Postavljeno kad provider potvrdi — osnova za 3-dnevno pravilo.</summary>
    public DateTime? AcceptedAt   { get; set; }

    /// <summary>
    /// True ako je booking Confirmed I prošlo je 3+ dana od potvrde.
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
