namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Isplaćen bonus sa liste čekanja — jedan red po adresi, jedan po nalogu.
///
/// Sajt je obećao 10 tokena svakome ko se upisao na listu pre izlaska i otvori
/// nalog istom adresom. Sama lista živi na sajtu (prijave.jsonl, vidi
/// <see cref="Infrastructure.ListaCekanja"/>); ovde je samo trag isplate.
///
/// NAMERNO BEZ STRANOG KLJUČA KA NALOGU. Red mora da preživi brisanje naloga:
/// inače bi „obriši nalog, otvori nov istom adresom" svaki put donosio novih
/// 10 tokena. Zato se ne čuva ni sama adresa nego njen heš — posle brisanja
/// naloga u bazi ne ostaje mejl, a ista adresa se i dalje prepoznaje.
///
/// Oba jedinstvena indeksa su brana od dvostruke isplate: EmailHash — ista
/// adresa ne može dvaput, UserId — isti nalog ne može dvaput.
/// </summary>
public class WaitlistBonus
{
    public int      Id         { get; set; }

    /// <summary>SHA-256 adrese (mala slova, bez razmaka), heksadecimalno.</summary>
    public string   EmailHash  { get; set; } = string.Empty;

    public string   UserId     { get; set; } = string.Empty;
    public decimal  Amount     { get; set; }

    /// <summary>Kad se upisao na sajtu — dokaz da je bio „pre izlaska".</summary>
    public DateTime SignedUpAt { get; set; }

    public DateTime AwardedAt  { get; set; } = DateTime.UtcNow;
}
