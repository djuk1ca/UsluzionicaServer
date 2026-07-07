namespace UsluzionicaServer.Domain.Entities;

public class ProviderProfile
{
    public int      Id            { get; set; }
    public string   UserId        { get; set; } = string.Empty;
    public string   Profession    { get; set; } = string.Empty;
    public string?  Bio           { get; set; }
    public string   Location      { get; set; } = string.Empty;
    public string?  CoverImageUrl { get; set; }
    public string?  Instagram     { get; set; }
    public decimal  AverageRating { get; set; } = 0m;
    public int      TotalReviews  { get; set; } = 0;
    public int      TotalListings { get; set; } = 0;
    public bool     IsVerified    { get; set; } = false;
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser               User               { get; set; } = null!;
    public ICollection<Listing>          Listings           { get; set; } = [];
    public ICollection<ProviderCategory> ProviderCategories { get; set; } = [];
    public ICollection<Review>           Reviews            { get; set; } = [];
}
