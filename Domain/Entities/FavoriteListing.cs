namespace UsluzionicaServer.Domain.Entities;

public class FavoriteListing
{
    public int      Id        { get; set; }
    public string   UserId    { get; set; } = string.Empty;
    public int      ListingId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User    { get; set; } = null!;
    public Listing         Listing { get; set; } = null!;
}
