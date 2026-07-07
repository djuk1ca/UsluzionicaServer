using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Reviews;

public sealed class ReviewDto
{
    public int      Id               { get; init; }
    public int      ListingId        { get; init; }
    public string   ListingTitle     { get; init; } = string.Empty;
    public int?     BookingRequestId { get; init; }
    public string   AuthorId         { get; init; } = string.Empty;
    public string   AuthorName       { get; init; } = string.Empty;
    public string?  AuthorImageUrl   { get; init; }
    public int      Stars            { get; init; }
    public string?  Comment          { get; init; }
    public DateTime CreatedAt        { get; init; }
}

public sealed class CreateReviewDto
{
    [Required]
    public int ListingId { get; init; }

    /// <summary>
    /// Opciono — ako je prosleđen mora biti Completed i mora
    /// pripadati korisniku koji piše recenziju.
    /// </summary>
    public int? BookingRequestId { get; init; }

    [Required]
    [Range(1, 5, ErrorMessage = "Ocena mora biti između 1 i 5.")]
    public int Stars { get; init; }

    [MaxLength(2000)]
    public string? Comment { get; init; }
}

/// <summary>
/// Agregirane statistike za providera — prosek i raspored po zvezdicama.
/// </summary>
public sealed class ReviewSummaryDto
{
    public int                  ProviderProfileId { get; init; }
    public decimal              AverageRating     { get; init; }
    public int                  TotalReviews      { get; init; }
    /// <summary>Broj recenzija po zvezdici: { 5: 10, 4: 3, 3: 1, 2: 0, 1: 0 }</summary>
    public Dictionary<int, int> StarBreakdown     { get; init; } = [];
}
