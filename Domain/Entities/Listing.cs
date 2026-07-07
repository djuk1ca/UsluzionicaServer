using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class Listing
{
    public int           Id                { get; set; }
    public int           ProviderProfileId { get; set; }
    public int           CategoryId        { get; set; }
    public string        Title             { get; set; } = string.Empty;
    public string        Description       { get; set; } = string.Empty;
    public string        Location          { get; set; } = string.Empty;
    public PriceMode     PriceMode         { get; set; }
    public decimal?      FixedPrice        { get; set; }
    public decimal?      PriceFrom         { get; set; }
    public decimal?      PriceTo           { get; set; }
    public ListingStatus Status            { get; set; } = ListingStatus.Active;
    public int           ViewCount         { get; set; } = 0;
    public bool          IsBoosted         { get; set; } = false;
    public DateTime?     BoostExpiresAt    { get; set; }
    public decimal       BoostScore        { get; set; } = 0m;
    public DateTime      CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime      UpdatedAt         { get; set; } = DateTime.UtcNow;

    // Navigation
    public ProviderProfile               ProviderProfile       { get; set; } = null!;
    public Category                      Category              { get; set; } = null!;
    public ICollection<ListingImage>     Images                { get; set; } = [];
    public ICollection<BookingRequest>   BookingRequests       { get; set; } = [];
    public ICollection<Review>           Reviews               { get; set; } = [];
    public ICollection<ListingBoost>     Boosts                { get; set; } = [];
    public ICollection<DiscountTokenOffer> DiscountOffers      { get; set; } = [];
}
