namespace UsluzionicaServer.DTOs.Users;

/// <summary>Body za PUT /api/users/me</summary>
public sealed class UpdateUserDto
{
    public string  FullName      { get; set; } = string.Empty;

    /// <summary>
    /// Mora biti vrednost iz liste srpskih opština (SerbianMunicipalities.All).
    /// Null znači da korisnik nije odabrao lokaciju.
    /// </summary>
    public string? LastKnownCity { get; set; }
}
