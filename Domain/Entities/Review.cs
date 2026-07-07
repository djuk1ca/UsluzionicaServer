namespace UsluzionicaServer.Domain.Entities;

public class Review
{
    public int      Id               { get; set; }
    public int      ListingId        { get; set; }
    public int?     BookingRequestId { get; set; }
    public string   AuthorId         { get; set; } = string.Empty;
    public int      Stars            { get; set; }
    public string?  Comment          { get; set; }
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;

    // Navigation
    public Listing         Listing        { get; set; } = null!;
    public BookingRequest? BookingRequest { get; set; }
    public ApplicationUser Author         { get; set; } = null!;
}
