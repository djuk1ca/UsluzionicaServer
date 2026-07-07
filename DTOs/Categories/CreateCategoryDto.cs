using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Categories;

/// <summary>DTO za kreiranje nove kategorije (admin only).</summary>
public sealed class CreateCategoryDto
{
    [Required, MaxLength(100)]
    public string Name      { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Slug      { get; set; } = string.Empty;

    /// <summary>
    /// Null → root (parent) kategorija.
    /// Popunjen ID → podkategorija datog parenta.
    /// </summary>
    public int? ParentId { get; set; }

    public int SortOrder { get; set; } = 0;
}

/// <summary>DTO za izmenu postojeće kategorije (admin only).</summary>
public sealed class UpdateCategoryDto
{
    [Required, MaxLength(100)]
    public string Name      { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Slug      { get; set; } = string.Empty;

    public int? ParentId { get; set; }

    public int SortOrder { get; set; } = 0;
}
