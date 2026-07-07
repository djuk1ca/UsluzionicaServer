using System.ComponentModel.DataAnnotations;
using UsluzionicaServer.DTOs.Listings;

namespace UsluzionicaServer.DTOs.Provider;

/// <summary>Kompletan provajder profil — za sopstveni i javni prikaz.</summary>
public sealed class ProviderProfileDto
{
    // Identifikatori
    public int    ProviderProfileId { get; set; }
    public string UserId            { get; set; } = string.Empty;

    // Korisnički podaci
    public string  FullName        { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string? CoverImageUrl   { get; set; }

    // Profesionalni podaci
    public string  Profession     { get; set; } = string.Empty;
    public string? Bio            { get; set; }
    public string  Location       { get; set; } = string.Empty;
    public string? Instagram      { get; set; }

    // Statistike
    public decimal AverageRating  { get; set; }
    public int     TotalReviews   { get; set; }
    public int     TotalListings  { get; set; }
    public bool    IsVerified     { get; set; }
    public DateTime CreatedAt     { get; set; }

    // Kategorije u kojima nudi usluge
    public List<ProviderCategoryDto> Categories { get; set; } = [];

    // Aktivni listinzi (za javni prikaz)
    public List<ListingDto> Listings { get; set; } = [];
}

public sealed class ProviderCategoryDto
{
    public int    CategoryId   { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string CategorySlug { get; set; } = string.Empty;
}

/// <summary>DTO za izmenu provajder profila.</summary>
public sealed class UpdateProviderDto
{
    [Required, MaxLength(200)]
    public string Profession { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Bio { get; set; }

    [Required, MaxLength(100)]
    public string Location { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Instagram { get; set; }

    [Required, MinLength(1)]
    public List<int> CategoryIds { get; set; } = [];
}

/// <summary>Kratak prikaz recenzije unutar provider profila.</summary>
public sealed class ReviewSummaryDto
{
    public int      Id        { get; set; }
    public string   AuthorName { get; set; } = string.Empty;
    public string?  AuthorImageUrl { get; set; }
    public int      Stars     { get; set; }
    public string?  Comment   { get; set; }
    public string   ListingTitle { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
