using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class BookingRequest
{
    public int           Id             { get; set; }
    public int           ListingId      { get; set; }
    public string        ClientId       { get; set; } = string.Empty;
    public string        ProviderUserId { get; set; } = string.Empty;
    public DateOnly      RequestedDate  { get; set; }
    public TimeOnly      RequestedTime  { get; set; }
    public string?       Notes          { get; set; }
    public BookingStatus Status         { get; set; } = BookingStatus.Pending;
    public DateTime      CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime      UpdatedAt      { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Postavljeno kad provider potvrdi zahtev (Status → Confirmed).
    /// Koristi se za 3-dnevno pravilo pri izvršavanju usluge.
    /// </summary>
    public DateTime?     AcceptedAt     { get; set; }

    // Navigation
    public Listing           Listing          { get; set; } = null!;
    public ApplicationUser   Client           { get; set; } = null!;
    public ApplicationUser   Provider         { get; set; } = null!;
    public ServiceExecution? ServiceExecution { get; set; }
    public Review?           Review           { get; set; }
}
