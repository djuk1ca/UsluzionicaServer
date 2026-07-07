using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.DTOs.Listings;

/// <summary>Kompletan prikaz jednog listinga (detalji + provajder).</summary>
public sealed class ListingDto
{
    public int          Id               { get; set; }
    public string       Title            { get; set; } = string.Empty;
    public string       Description      { get; set; } = string.Empty;
    public string       Location         { get; set; } = string.Empty;
    public PriceMode    PriceMode        { get; set; }
    public decimal?     FixedPrice       { get; set; }
    public decimal?     PriceFrom        { get; set; }
    public decimal?     PriceTo          { get; set; }
    public ListingStatus Status          { get; set; }
    public int          ViewCount        { get; set; }
    public bool         IsBoosted        { get; set; }
    public DateTime     CreatedAt        { get; set; }
    public DateTime     UpdatedAt        { get; set; }

    // Kategorija
    public int          CategoryId       { get; set; }
    public string       CategoryName     { get; set; } = string.Empty;
    public string       CategorySlug     { get; set; } = string.Empty;

    // Slike
    public List<ListingImageDto> Images  { get; set; } = [];

    // Skraćeni prikaz provajdera
    public ProviderSummaryDto Provider   { get; set; } = null!;
}

public sealed class ListingImageDto
{
    public int    Id        { get; set; }
    public string ImageUrl  { get; set; } = string.Empty;
    public int    SortOrder { get; set; }
}

/// <summary>Podaci o provajderu koji se prikazuju unutar kartice listinga.</summary>
public sealed class ProviderSummaryDto
{
    public int     ProviderProfileId { get; set; }
    public string  UserId            { get; set; } = string.Empty;
    public string  FullName          { get; set; } = string.Empty;
    public string  Profession        { get; set; } = string.Empty;
    public string? ProfileImageUrl   { get; set; }
    public decimal AverageRating     { get; set; }
    public int     TotalReviews      { get; set; }
    public bool    IsVerified        { get; set; }
}
