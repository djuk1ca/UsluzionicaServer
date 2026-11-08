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
    /// Koristi se za pravilo čekanja (Booking:ExecuteAfterDays) pri izvršavanju usluge.
    /// </summary>
    public DateTime?     AcceptedAt     { get; set; }

    // ── Skinuto čekanje do izvršenja ───────────────────────────────────────
    // Kad je postavljeno, ZAMENJUJE AcceptedAt + Booking:ExecuteAfterDays:
    // „Izvršeno" je dozvoljeno od ovog trenutka. Postavlja ga admin za
    // korisnike za koje zna da su pouzdani, ili sam server pri potvrdi kad su
    // oba učesnika označena kao pouzdana (ApplicationUser.IsTrusted).
    // Jedino pravilo koje to čita je BookingService.IzvrsenjeDozvoljenoOd.

    /// <summary>Od kad je „Izvršeno" dozvoljeno mimo čekanja. Null = važi čekanje.</summary>
    public DateTime?     ExecuteAllowedAt   { get; set; }

    /// <summary>
    /// Admin koji je skinuo čekanje (radi revizije). Null uz postavljen
    /// <see cref="ExecuteAllowedAt"/> znači da je čekanje skinuto automatski,
    /// jer su oba učesnika pouzdana.
    /// </summary>
    public string?       ExecuteAllowedById { get; set; }

    // Navigation
    public Listing           Listing          { get; set; } = null!;
    public ApplicationUser   Client           { get; set; } = null!;
    public ApplicationUser   Provider         { get; set; } = null!;
    public ServiceExecution? ServiceExecution { get; set; }
    /// <summary>Do dve ocene — klijent uslugodavca i uslugodavac klijenta.</summary>
    public ICollection<Review> Reviews        { get; set; } = [];
}
