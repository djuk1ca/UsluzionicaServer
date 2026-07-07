namespace UsluzionicaServer.DTOs.Categories;

/// <summary>
/// Kategorija u odgovoru API-ja.
/// Kada je parent kategorija — sadrži popunjenu listu Children.
/// Kada je podkategorija — Children je prazna lista.
/// </summary>
public sealed class CategoryDto
{
    public int              Id        { get; set; }
    public string           Name      { get; set; } = string.Empty;
    public string           Slug      { get; set; } = string.Empty;
    public int?             ParentId  { get; set; }
    public int              SortOrder { get; set; }
    public List<CategoryDto> Children  { get; set; } = [];
}
