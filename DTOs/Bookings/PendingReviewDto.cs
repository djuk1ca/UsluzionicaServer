namespace UsluzionicaServer.DTOs.Bookings;

/// <summary>
/// Izvršena usluga ili posao koji korisnik još nije ocenio.
///
/// Namenjen podsetniku koji iskoči kad se aplikacija otvori posle izvršenja,
/// i „Čeka ocenu" u Mojim poslovima. Zato nosi samo ono što stane u tu
/// karticu i ono što treba za odlazak na ocenu.
/// </summary>
public sealed class PendingReviewDto
{
    /// <summary>„Booking" ili „Job".</summary>
    public string   Kind         { get; set; } = "Booking";

    public int?     BookingId    { get; set; }
    public int?     ListingId    { get; set; }
    public int?     JobId        { get; set; }

    /// <summary>Naslov oglasa ili posla.</summary>
    public string   ListingTitle { get; set; } = string.Empty;

    /// <summary>Ime druge strane — koga korisnik ocenjuje (ime polja iz vremena kad je to bio uvek uslugodavac).</summary>
    public string   ProviderName { get; set; } = string.Empty;

    /// <summary>„Provider" ili „Client" — u kojoj ulozi korisnik ocenjuje drugu stranu.</summary>
    public string   SubjectRole  { get; set; } = "Provider";

    public DateTime CompletedAt  { get; set; }
    public DateTime ReviewDeadline { get; set; }

    /// <summary>Druga strana je već ocenila — ocena čeka samo ovog korisnika.</summary>
    public bool     OtherSideReviewed { get; set; }
}
