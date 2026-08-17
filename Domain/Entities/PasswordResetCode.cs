namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Jednokratni kod od 6 cifara za reset lozinke.
///
/// Zašto sopstvena tabela umesto Identity `GeneratePasswordResetTokenAsync`:
/// taj token je dugačak i neprikladan za ručni prepis u aplikaciju, a
/// Identity-jev TOTP provajder (koji daje 6 cifara) ima fiksan prozor od 3
/// minuta — prekratko ako se email zadrži u redu za slanje. Ovako imamo pun
/// nadzor nad rokom trajanja, brojem pokušaja i jednokratnošću.
///
/// Kod se čuva HEŠIRAN (SHA-256). Ako baza procuri, kodovi nisu direktno
/// upotrebljivi.
/// </summary>
public class PasswordResetCode
{
    public int      Id        { get; set; }
    public string   UserId    { get; set; } = string.Empty;

    /// <summary>SHA-256 heš koda, Base64. Nikad se ne čuva čist kod.</summary>
    public string   CodeHash  { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Popunjeno kad je kod iskorišćen — sprečava ponovnu upotrebu.</summary>
    public DateTime? UsedAt   { get; set; }

    /// <summary>Broj neuspelih provera. Brani od pogađanja koda.</summary>
    public int      Attempts  { get; set; }

    public ApplicationUser User { get; set; } = null!;
}
