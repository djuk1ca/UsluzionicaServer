namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Jedan grad u kome uslugodavac pruža uslugu iz oglasa. Oglas ih može imati
/// više (najviše ListingService.MaxGradova), redom kojim ih je upisao.
///
/// Posebna tabela, a ne lista u jednoj koloni: filter grada mora da bude
/// indeksirani prefiks nad SVAKIM gradom posebno („Beograd" → sve beogradske
/// opštine), a to nad spojenim tekstom ne ide.
/// </summary>
public class ListingCity
{
    public int    Id        { get; set; }
    public int    ListingId { get; set; }

    /// <summary>Naziv sa zvanične liste opština, u njenom pisanju.</summary>
    public string City      { get; set; } = string.Empty;

    /// <summary>Fold(City). Održava ga AppDbContext.SaveChanges kroz SearchIndexer.</summary>
    public string SearchCity { get; set; } = string.Empty;

    public int    SortOrder { get; set; } = 0;

    // Navigation
    public Listing Listing { get; set; } = null!;
}
