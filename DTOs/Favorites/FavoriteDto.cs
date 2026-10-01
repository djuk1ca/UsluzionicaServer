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
    /// <summary>
    /// Prva slika oglasa.
    ///
    /// Ime MORA da se završava na „ImageUrl". <c>MediaUrlJsonModifier</c>
    /// relativne putanje (<c>/uploads/...</c>) pretvara u pune URL-ove samo za
    /// polja sa tim sufiksom. Ranije se zvalo <c>ThumbnailUrl</c>, pa je klijent
    /// dobijao golu relativnu putanju — WebView je razrešava prema svom lokalnom
    /// poreklu, a ne prema API-ju, i slika u sačuvanim oglasima se nikad nije
    /// prikazala.
    /// </summary>
    public string?  ThumbnailImageUrl { get; init; }
    public string   ProviderName  { get; init; } = string.Empty;
    public bool     IsBoosted     { get; init; }
    public decimal  BoostScore    { get; init; }
    public DateTime? BoostExpiresAt { get; init; }
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
