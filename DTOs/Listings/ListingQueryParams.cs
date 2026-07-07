namespace UsluzionicaServer.DTOs.Listings;

/// <summary>
/// Query parametri za pretragu listinga.
/// Sve vrednosti su opcionalne — ako se ne prosleđuju, vraćaju se svi aktivni listinzi.
/// </summary>
public sealed class ListingQueryParams
{
    /// <summary>Fulltext pretraga u naslovu i opisu.</summary>
    public string? Q            { get; set; }

    /// <summary>Filter po slug-u kategorije (npr. "beauty", "majstori").</summary>
    public string? CategorySlug { get; set; }

    /// <summary>Filter po gradu (mora biti validna srpska opština).</summary>
    public string? City         { get; set; }

    /// <summary>Stranica (1-based). Default: 1.</summary>
    public int Page             { get; set; } = 1;

    /// <summary>Broj rezultata po stranici. Default: 20, max: 50.</summary>
    public int PageSize         { get; set; } = 20;
}

/// <summary>
/// Generic wrapper za paginovane rezultate.
/// </summary>
public sealed class PagedResult<T>
{
    public List<T> Items    { get; set; } = [];
    public int     Total    { get; set; }
    public int     Page     { get; set; }
    public int     PageSize { get; set; }
    public int     Pages    => PageSize > 0 ? (int)Math.Ceiling((double)Total / PageSize) : 0;
}
