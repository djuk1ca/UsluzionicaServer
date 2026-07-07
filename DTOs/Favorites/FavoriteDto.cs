namespace UsluzionicaServer.DTOs.Favorites;

/// <summary>Vraća se pri toggle operaciji — da li je oglas sada u omiljenima.</summary>
public sealed class FavoriteStatusDto
{
    public bool IsFavorited { get; init; }
}

/// <summary>Slim prikaz omiljenog oglasa — za home page listu.</summary>
public sealed class FavoriteListingDto
{
    public int      FavoriteId    { get; init; }
    public int      ListingId     { get; init; }
    public string   Title         { get; init; } = string.Empty;
    public string   Location      { get; init; } = string.Empty;
    public string   CategoryName  { get; init; } = string.Empty;
    public string   CategorySlug  { get; init; } = string.Empty;
    public string   PriceMode     { get; init; } = string.Empty;
    public decimal? FixedPrice    { get; init; }
    public decimal? PriceFrom     { get; init; }
    public decimal? PriceTo       { get; init; }
    public string?  ThumbnailUrl  { get; init; }
    public string   ProviderName  { get; init; } = string.Empty;
    public bool     IsBoosted     { get; init; }
    public DateTime SavedAt       { get; init; }
}

/// <summary>Slim prikaz omiljenog uslugodavaca — za home page listu.</summary>
public sealed class FavoriteProviderDto
{
    public int      FavoriteId        { get; init; }
    public int      ProviderProfileId { get; init; }
    public string   UserId            { get; init; } = string.Empty;
    public string   FullName          { get; init; } = string.Empty;
    public string   Profession        { get; init; } = string.Empty;
    public string   Location          { get; init; } = string.Empty;
    public string?  ProfileImageUrl   { get; init; }
    public decimal  AverageRating     { get; init; }
    public int      TotalReviews      { get; init; }
    public int      TotalListings     { get; init; }
    public bool     IsVerified        { get; init; }
    public DateTime SavedAt           { get; init; }
}
