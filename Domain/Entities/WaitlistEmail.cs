namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Trag jednog mejla poslatog adresi sa liste čekanja — jedan red po adresi i
/// kampanji (npr. „ios-izlazak").
///
/// Ovo je brana od duplikata: red se upisuje PRE slanja (rezervacija), a
/// jedinstveni indeks (Campaign, EmailHash) garantuje da dva pokretanja — ili
/// dva istovremena zahteva — ne mogu poslati isti mejl istoj adresi.
///
/// Kao <see cref="WaitlistBonus"/>: bez adrese, samo njen heš. Lista živi na
/// sajtu; ovde je samo trag da je poruka otišla.
///
/// Stanja:
///   SentAt != null                 → poslato
///   FailedAt != null, SentAt null  → SMTP je odbio; ponovo se šalje samo na
///                                    izričit zahtev (ponoviNeuspele=true)
///   oba null                       → rezervisano, slanje je u toku (ili je
///                                    proces pao usred slanja — tada se NE
///                                    šalje ponovo, jer je poruka možda otišla)
/// </summary>
public class WaitlistEmail
{
    public int       Id         { get; set; }

    public string    Campaign   { get; set; } = string.Empty;

    /// <summary>SHA-256 adrese (mala slova, bez razmaka), heksadecimalno.</summary>
    public string    EmailHash  { get; set; } = string.Empty;

    public DateTime  ReservedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt     { get; set; }
    public DateTime? FailedAt   { get; set; }

    /// <summary>Samo vrsta greške (npr. SmtpCommandException) — bez adrese i bez poruke servera.</summary>
    public string?   Error      { get; set; }
}

/// <summary>
/// Odjava sa liste čekanja (link u mejlu). Jedan red po adresi, opet samo heš.
///
/// API ne sme da menja listu sajta (montirana je samo za čitanje), pa se
/// odjava pamti ovde i svako sledeće slanje je preskače. Bonus NE gubi —
/// odjava se tiče mejlova, ne obećanja.
/// </summary>
public class WaitlistOptOut
{
    public int      Id          { get; set; }
    public string   EmailHash   { get; set; } = string.Empty;
    public DateTime OptedOutAt  { get; set; } = DateTime.UtcNow;
}
