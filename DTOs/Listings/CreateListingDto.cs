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

    /// <summary>
    /// Gradovi usluge, sa zvanične liste opština, najviše
    /// ListingService.MaxGradova. Bar jedan je obavezan kad oglas nije online
    /// — to proverava ListingService.ResolveCities, ne atribut, jer zavisi od
    /// IsOnline. Null = stari klijent koji šalje samo <see cref="Location"/>.
    /// </summary>
    public List<string>? Cities { get; set; }

    /// <summary>
    /// Stari oblik: jedan grad. Koristi se samo kad <see cref="Cities"/> nije
    /// poslat. Nullable namerno: uz &lt;Nullable&gt; MVC bi ne-nullable string
    /// tretirao kao [Required] i odbio online oglas bez grada.
    /// </summary>
    [MaxLength(100)]
    public string? Location   { get; set; }

    /// <summary>Usluga se pruža na daljinu. Tada gradovi nisu obavezni.
    /// Null (stari klijent) = nije online.</summary>
    public bool? IsOnline     { get; set; }

    [Required]
    public int CategoryId     { get; set; }

    [Required]
    public PriceMode PriceMode { get; set; }

    /// <summary>Obavezno ako je PriceMode = Fixed.</summary>
    public decimal? FixedPrice { get; set; }

    /// <summary>Obavezno ako je PriceMode = Range.</summary>
    public decimal? PriceFrom  { get; set; }

    /// <summary>Opciono i za PriceMode = Range („od 7.000 RSD"). Ako je upisano, mora biti ≥ PriceFrom.</summary>
    public decimal? PriceTo    { get; set; }
}

/// <summary>DTO za izmenu postojećeg listinga (ista polja).</summary>
public sealed class UpdateListingDto
{
    [Required, MaxLength(200)]
    public string Title       { get; set; } = string.Empty;

    [Required, MaxLength(5000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Isto kao u CreateListingDto. Null = stari klijent: gradovi
    /// se ne diraju dok <see cref="Location"/> ostaje prvi grad.</summary>
    public List<string>? Cities { get; set; }

    [MaxLength(100)]
    public string? Location   { get; set; }

    /// <summary>Null (stari klijent) = ostaje kako je bilo.</summary>
    public bool? IsOnline     { get; set; }

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

/// <summary>
/// DTO za promenu redosleda slika oglasa. Prvi id postaje glavna slika.
/// Mora sadržati tačno sve slike oglasa, svaku jednom.
/// </summary>
public sealed class ReorderListingImagesDto
{
    [Required]
    public List<int> ImageIds { get; set; } = [];
}
