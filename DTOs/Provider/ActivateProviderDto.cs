using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Provider;

/// <summary>
/// Podaci potrebni za aktivaciju provajder naloga.
/// Korisnik mora imati verifikovan email.
/// </summary>
public sealed class ActivateProviderDto
{
    [Required, MaxLength(200)]
    public string Profession { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Bio { get; set; }

    [Required, MaxLength(100)]
    public string Location { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Instagram { get; set; }

    /// <summary>
    /// Lista ID-jeva kategorija u kojima provajder nudi usluge.
    /// Minimum 1, maksimum 10.
    /// </summary>
    [Required, MinLength(1)]
    public List<int> CategoryIds { get; set; } = [];
}
