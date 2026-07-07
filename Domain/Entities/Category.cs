namespace UsluzionicaServer.Domain.Entities;

public class Category
{
    public int      Id        { get; set; }
    public string   Name      { get; set; } = string.Empty;
    public string   Slug      { get; set; } = string.Empty;
    public int?     ParentId  { get; set; }
    public int      SortOrder { get; set; } = 0;

    // Navigation
    public Category?              Parent            { get; set; }
    public ICollection<Category>  Children          { get; set; } = [];
    public ICollection<Listing>   Listings          { get; set; } = [];
    public ICollection<ProviderCategory> ProviderCategories { get; set; } = [];
}
