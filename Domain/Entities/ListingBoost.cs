namespace UsluzionicaServer.Domain.Entities;

public class ListingBoost
{
    public int      Id           { get; set; }
    public int      ListingId    { get; set; }
    public string   UserId       { get; set; } = string.Empty;
    public decimal  TokensSpent  { get; set; }
    public int      DurationDays { get; set; }
    public DateTime StartsAt     { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt    { get; set; }
    public bool     IsActive     { get; set; } = true;

    // Navigation
    public Listing         Listing { get; set; } = null!;
    public ApplicationUser User    { get; set; } = null!;
}
