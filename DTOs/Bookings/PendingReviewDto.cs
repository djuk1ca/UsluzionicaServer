namespace UsluzionicaServer.DTOs.Bookings;

/// <summary>
/// Izvršena usluga koju klijent još nije ocenio.
///
/// Namenjen jednom jedinom ekranu — podsetniku koji iskoči kad se aplikacija
/// otvori posle izvršene usluge. Zato nosi samo ono što stane u tu karticu i
/// ono što treba za odlazak na stranicu recenzije.
/// </summary>
public sealed class PendingReviewDto
{
    public int      BookingId    { get; set; }
    public int      ListingId    { get; set; }
    public string   ListingTitle { get; set; } = string.Empty;
    public string   ProviderName { get; set; } = string.Empty;
    public DateTime CompletedAt  { get; set; }
}
