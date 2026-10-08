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
        var dana  = bookings.ExecuteAfterDays;
        var granica = DateTime.UtcNow.AddDays(-dana);

        var dospeli = await db.BookingRequests.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed
                     && b.AcceptedAt != null
                     && b.AcceptedAt <= granica
                     && !db.Notifications.Any(n => n.Kind == NotificationKind.BookingExecutable
                                                && n.ReferenceId == b.Id
                                                && n.UserId == b.ProviderUserId))
            .Select(b => new { b.Id, b.ProviderUserId, Naslov = b.Listing.Title, Klijent = b.Client.FullName })
            .Take(200)
            .ToListAsync(ct);

        foreach (var z in dospeli)
        {
            await notifications.SendAsync(
                z.ProviderUserId,
                NotificationKind.BookingExecutable,
                "Uslugu možeš označiti kao izvršenu",
                $"Prošlo je {dana} dana od prihvatanja zahteva „{z.Naslov}“ ({z.Klijent}). " +
                "Označi uslugu kao izvršenu — tada i ti i klijent dobijate tokene.",
                z.Id);
        }

        if (dospeli.Count > 0)
            logger.LogInformation("PodsetnikZaIzvrsenje: poslato {Broj} podsetnika.", dospeli.Count);

        return dospeli.Count;
    }
}
