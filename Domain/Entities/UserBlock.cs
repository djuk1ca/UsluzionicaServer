namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Jedan korisnik je blokirao drugog.
///
/// BLOKADA DELUJE SIMETRIČNO, iako je red usmeren.
/// Ako A blokira B, nijedan ne vidi sadržaj onog drugog i ne mogu da
/// komuniciraju. Red pamti KO je blokirao (da samo on može da odblokira), ali
/// svaka provera vidljivosti gleda oba smera — vidi <c>BlockService</c>.
///
/// Asimetrična blokada bi bila polumera: B bi i dalje gledao A-ove oglase, pisao
/// mu i prijavljivao ga. Onome ko blokira to ne rešava ništa.
/// </summary>
public class UserBlock
{
    public int      Id        { get; set; }

    /// <summary>Ko je blokirao. Samo on može da odblokira.</summary>
    public string   BlockerId { get; set; } = string.Empty;

    /// <summary>Koga je blokirao.</summary>
    public string   BlockedId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser Blocker { get; set; } = null!;
    public ApplicationUser Blocked { get; set; } = null!;
}
