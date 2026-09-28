namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// FCM token jednog uređaja, vezan za korisnika koji je na njemu prijavljen.
///
/// ZAŠTO JE <see cref="Token"/> JEDINSTVEN, I ZAŠTO TO NIJE FORMALNOST
///
/// Jedan uređaj daje jedan FCM token. Ako se na istom telefonu odjavi Marko i
/// prijavi Ana, Firebase vraća <b>isti</b> token — on pripada instalaciji
/// aplikacije, ne nalogu.
///
/// Bez jedinstvenog indeksa nastala bi dva reda sa istim tokenom, jedan za
/// Marka i jedan za Anu, pa bi <b>Marko dobijao Anine notifikacije</b> — sa
/// imenom pošiljaoca i tekstom poruke u telu obaveštenja.
///
/// Zato upis nije „dodaj" nego „preuzmi": red sa tim tokenom se pronađe i
/// <see cref="UserId"/> se prepiše na novog vlasnika.
/// </summary>
public class DeviceToken
{
    public int Id { get; set; }

    /// <summary>Korisnik koji je TRENUTNO prijavljen na tom uređaju.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>FCM registracioni token. Jedinstven na nivou tabele.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary><c>android</c> ili <c>ios</c> — za dijagnostiku i za slučaj da
    /// platforme ikad zatraže različit oblik poruke.</summary>
    public string Platform { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Obnavlja se pri svakoj prijavi i pri osvežavanju tokena.
    ///
    /// Služi za čišćenje: FCM token uređaja koji se mesecima nije javio je
    /// gotovo sigurno mrtav, a svako slanje na njega je uzaludan poziv.
    /// </summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser? User { get; set; }
}
