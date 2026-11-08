using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Javlja uslugodavcu kad istekne čekanje posle prihvatanja i usluga može da se
/// označi kao izvršena.
///
/// ZAŠTO POSTOJI
///
/// „Izvršeno" je zaključano Booking:ExecuteAfterDays dana posle prihvatanja
/// (anti-farming tokena). Posle nedelju dana niko ne pamti da treba da se vrati
/// i pritisne dugme — a dok ga ne pritisne, ni klijent ni uslugodavac ne
/// dobijaju tokene, a klijent ne može da ostavi ocenu. Podsetnik zatvara krug.
///
/// JEDNOM PO ZAHTEVU
///
/// Da li je podsetnik već poslat, čita se iz samih obaveštenja (vrsta +
/// ReferenceId = id zahteva), bez nove kolone u bazi. Ako uslugodavac obriše
/// obaveštenje, podsetnik se ne ponavlja — obaveštenja se ne brišu, samo
/// označavaju kao pročitana.
///
/// SKINUTO ČEKANJE
///
/// Rok čita isto pravilo kao BookingService.IzvrsenjeDozvoljenoOd: termin sa
/// skinutim čekanjem (ExecuteAllowedAt) dospeva odmah. Kad čekanje skine
/// admin, obaveštenje šalje sam BookingService, pa ovde ne stiže drugi put;
/// kad se skine automatski (oba učesnika pouzdana), javlja se ovaj podsetnik.
/// </summary>
public sealed class PodsetnikZaIzvrsenje(
    AppDbContext                  db,
    NotificationService           notifications,
    BookingService                bookings,
    ILogger<PodsetnikZaIzvrsenje> logger)
{
    /// <summary>Šalje sve dospele podsetnike; vraća koliko ih je poslato.</summary>
    public async Task<int> PosaljiAsync(CancellationToken ct = default)
    {
        var dana    = bookings.ExecuteAfterDays;
        var sada    = DateTime.UtcNow;
        var granica = sada.AddDays(-dana);

        // Ista logika kao BookingService.IzvrsenjeDozvoljenoOd, u SQL-u.
        var dospeli = await db.BookingRequests.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed
                     && ((b.ExecuteAllowedAt != null && b.ExecuteAllowedAt <= sada)
                      || (b.ExecuteAllowedAt == null && b.AcceptedAt != null && b.AcceptedAt <= granica))
                     && !db.Notifications.Any(n => n.Kind == NotificationKind.BookingExecutable
                                                && n.ReferenceId == b.Id
                                                && n.UserId == b.ProviderUserId))
            .Select(b => new { b.Id, b.ProviderUserId, Naslov = b.Listing.Title, Klijent = b.Client.FullName,
                               Skinuto = b.ExecuteAllowedAt != null })
            .Take(200)
            .ToListAsync(ct);

        foreach (var z in dospeli)
        {
            await notifications.SendAsync(
                z.ProviderUserId,
                NotificationKind.BookingExecutable,
                "Uslugu možeš označiti kao izvršenu",
                z.Skinuto
                    ? TekstOdmah(z.Naslov, z.Klijent)
                    : $"Prošlo je {dana} dana od prihvatanja zahteva „{z.Naslov}“ ({z.Klijent}). " +
                      "Označi uslugu kao izvršenu — tada i ti i klijent dobijate tokene.",
                z.Id);
        }

        if (dospeli.Count > 0)
            logger.LogInformation("PodsetnikZaIzvrsenje: poslato {Broj} podsetnika.", dospeli.Count);

        return dospeli.Count;
    }

    /// <summary>Tekst obaveštenja kad je čekanje skinuto (admin ili pouzdani korisnici).</summary>
    public static string TekstOdmah(string naslov, string klijent) =>
        $"Za zahtev „{naslov}“ ({klijent}) ne moraš da čekaš — uslugu možeš odmah " +
        "da označiš kao izvršenu. Tada i ti i klijent dobijate tokene.";
}
