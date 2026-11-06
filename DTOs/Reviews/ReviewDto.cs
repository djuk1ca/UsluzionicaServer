using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Reviews;

public sealed class ReviewDto
{
    public int      Id               { get; init; }

    /// <summary>0 za ocene posla — starije verzije aplikacije očekuju broj.</summary>
    public int      ListingId        { get; init; }

    /// <summary>Naslov oglasa ili posla u trenutku ocene.</summary>
    public string   ListingTitle     { get; init; } = string.Empty;
    public int?     BookingRequestId { get; init; }
    public int?     JobId            { get; init; }

    /// <summary>„Booking" ili „Job".</summary>
    public string   Context          { get; init; } = "Booking";

    /// <summary>„Provider" ili „Client" — u kojoj ulozi je ocenjeni ocenjen.</summary>
    public string   SubjectRole      { get; init; } = "Provider";

    public string   AuthorId         { get; init; } = string.Empty;
    public string   AuthorName       { get; init; } = string.Empty;
    public string?  AuthorImageUrl   { get; init; }
    public int      Stars            { get; init; }
    public string?  Comment          { get; init; }
    public DateTime CreatedAt        { get; init; }

    /// <summary>Samo u odgovoru na ocenu: false = čeka drugu stranu ili rok.</summary>
    public bool      IsPublished     { get; init; } = true;
    public DateTime? RevealAt        { get; init; }
}

/// <summary>
/// Ocena posle izvršene usluge (<see cref="BookingRequestId"/>) ili izvršenog
/// posla (<see cref="JobId"/>) — tačno jedno. Server sam zna ko je druga strana.
/// </summary>
public sealed class CreateReviewDto
{
    /// <summary>Opciono, samo provera — oglas se čita iz rezervacije.</summary>
    public int? ListingId { get; init; }

    public int? BookingRequestId { get; init; }

    public int? JobId { get; init; }

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

/// <summary>Ocene korisnika kao naručioca — sheet uz ime na poslu, zahtevu i u četu.</summary>
public sealed class ClientReviewsDto
{
    public string          UserId        { get; init; } = string.Empty;
    public string          FullName      { get; init; } = string.Empty;
    public decimal         AverageRating { get; init; }
    public int             TotalReviews  { get; init; }
    public List<ReviewDto> Items         { get; init; } = [];
}
