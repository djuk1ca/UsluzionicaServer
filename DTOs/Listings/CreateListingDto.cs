using System.ComponentModel.DataAnnotations;
using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.DTOs.Listings;

/// <summary>DTO za kreiranje novog listinga.</summary>
public sealed class CreateListingDto
{
    [Required, MaxLength(200)]
    public string Title       { get; set; } = string.Empty;

    [Required, MaxLength(5000)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Location    { get; set; } = string.Empty;

    [Required]
    public int CategoryId     { get; set; }

    [Required]
    public PriceMode PriceMode { get; set; }

    /// <summary>Obavezno ako je PriceMode = Fixed.</summary>
    public decimal? FixedPrice { get; set; }

    /// <summary>Obavezno ako je PriceMode = Range.</summary>
    public decimal? PriceFrom  { get; set; }

    /// <summary>Obavezno ako je PriceMode = Range.</summary>
    public decimal? PriceTo    { get; set; }
}

/// <summary>DTO za izmenu postojećeg listinga (ista polja).</summary>
public sealed class UpdateListingDto
{
    [Required, MaxLength(200)]
    public string Title       { get; set; } = string.Empty;

    [Required, MaxLength(5000)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Location    { get; set; } = string.Empty;

    [Required]
    public int CategoryId     { get; set; }

    [Required]
    public PriceMode PriceMode { get; set; }

    public decimal? FixedPrice { get; set; }
    public decimal? PriceFrom  { get; set; }
    public decimal? PriceTo    { get; set; }
}

/// <summary>DTO za promenu statusa listinga (Active / Paused / Archived).</summary>
public sealed class UpdateListingStatusDto
{
    [Required]
    public string Status { get; set; } = string.Empty;
}
