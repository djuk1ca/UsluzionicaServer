using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Notifications;

/// <summary>Prijava uređaja za push notifikacije.</summary>
public sealed class RegisterDeviceDto
{
    /// <summary>FCM registracioni token. Dug je oko 160 znakova, ali FCM ne
    /// garantuje dužinu, pa je granica postavljena naviše.</summary>
    [Required]
    [StringLength(255, MinimumLength = 10)]
    public string Token { get; set; } = string.Empty;

    /// <summary><c>android</c> ili <c>ios</c>.</summary>
    [Required]
    [RegularExpression("^(android|ios)$",
        ErrorMessage = "Platforma mora biti 'android' ili 'ios'.")]
    public string Platform { get; set; } = string.Empty;
}

/// <summary>Odjava uređaja — zove se pri odjavi korisnika.</summary>
public sealed class UnregisterDeviceDto
{
    [Required]
    [StringLength(255, MinimumLength = 10)]
    public string Token { get; set; } = string.Empty;
}
