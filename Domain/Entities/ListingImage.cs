namespace UsluzionicaServer.Domain.Entities;

public class ListingImage
{
    public int    Id        { get; set; }
    public int    ListingId { get; set; }
    public string ImageUrl  { get; set; } = string.Empty;
    public int    SortOrder { get; set; } = 0;

    // Navigation
    public Listing Listing { get; set; } = null!;
}
