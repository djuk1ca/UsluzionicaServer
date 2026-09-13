using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Deaktivacija i vraćanje naloga, sa SVIM posledicama.
///
/// Postoji kao zaseban servis zato što deaktivacija ima tri dela koja moraju
/// da idu zajedno, a zovu je dva pozivaoca (admin ručno i rešavanje prijave).
/// Da je logika prepisana na oba mesta, jedno bi pre ili kasnije zaboravilo
/// jedan deo — i to tiho.
///
/// ŠTA JE RANIJE FALILO (zatečeno stanje pre ovog servisa):
///
/// 1. <c>IsActive</c> se proveravao SAMO pri prijavi i osvežavanju tokena
///    (AuthService). Oglasi deaktiviranog korisnika ostajali su u pretrazi —
///    dakle baniš nalog, a sadržaj zbog kog si ga banovao i dalje stoji.
///
/// 2. Refresh tokeni se nisu poništavali. Access token traje 60 minuta, pa je
///    banovan korisnik nastavljao da radi do sat vremena.
///
/// 3. <c>AdminService.DeactivateUserAsync</c> je bio TOGGLE
///    (<c>SetProperty(u => !u.IsActive)</c>). Admin koji dvaput klikne na
///    prijavu vratio bi nalog, a da to nigde ne vidi. Zato je ovde eksplicitno
///    „deaktiviraj" ili „vrati", nikad „obrni".
/// </summary>
public sealed class UserModerationService(
    AppDbContext                   db,
    NotificationService            notifications,
    ILogger<UserModerationService> logger)
{
    /// <summary>
    /// Deaktivira nalog: gasi pristup, sklanja sadržaj i poništava tokene.
    /// </summary>
    /// <param name="razlog">
    /// Ide korisniku u notifikaciju. Bez razloga korisnik ne zna šta je
    /// prekršio niti na šta da se žali.
    /// </param>
    public async Task<(bool Success, string? Error)> DeaktivirajAsync(
        string userId, string? razlog = null)
    {
        var postoji = await db.Users.AnyAsync(u => u.Id == userId);
        if (!postoji)
            return (false, "Korisnik nije pronađen.");

        // 1. Gasi prijavu. Uslovni UPDATE: ako je već deaktiviran, vraća 0 i
        //    ostatak se preskače — ponovljena deaktivacija ne šalje drugu
        //    notifikaciju i ne arhivira oglase po drugi put.
        var promenjeno = await db.Users
            .Where(u => u.Id == userId && u.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        if (promenjeno == 0)
            return (true, null);   // već je deaktiviran — traženo stanje postoji

        // 2. Sklanja sadržaj sa ekrana. Bez ovoga ostaje rupa opisana gore.
        //
        //    Arhiviranje, ne brisanje: oglas visi na rezervacijama, recenzijama
        //    i token transakcijama, pa bi DELETE srušio referencijalni integritet
        //    ili povukao za sobom istoriju koja mora da ostane.
        var arhivirano = await db.Listings
            .Where(l => l.ProviderProfile.UserId == userId &&
                        l.Status != ListingStatus.Archived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status,    ListingStatus.Archived)
                .SetProperty(l => l.UpdatedAt, DateTime.UtcNow));

        // 3. Poništava refresh tokene. Access token i dalje važi do isteka
        //    (najviše 60 min), ali se posle toga ne može produžiti.
        //
        //    Kraći access token bi zatvorio i taj prozor, ali bi poskupeo svaki
        //    zahtev u aplikaciji. Za ovu fazu je prihvatljivo; ako zatreba brže,
        //    rešenje je lista poništenih u Redisu, ne skraćivanje svima.
        var ponisteno = await db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true));

        logger.LogWarning(
            "Nalog {UserId} deaktiviran. Arhivirano oglasa: {Arhivirano}, " +
            "poništeno tokena: {Ponisteno}. Razlog: {Razlog}",
            userId, arhivirano, ponisteno, razlog ?? "(nije naveden)");

        await notifications.SendAsync(
            userId,
            NotificationKind.AccountDeactivated,
            "Nalog je deaktiviran",
            razlog is null
                ? "Vaš nalog je deaktiviran zbog kršenja uslova korišćenja."
                : $"Vaš nalog je deaktiviran: {razlog}");

        return (true, null);
    }

    /// <summary>
    /// Vraća nalog u rad.
    ///
    /// NAMERNO NE VRAĆA OGLASE IZ ARHIVE. Arhiviranje je jednosmerno sa naše
    /// strane: u trenutku vraćanja se ne zna koji su oglasi bili arhivirani
    /// zbog kazne, a koje je vlasnik sam arhivirao ranije. Vlasnik svoje oglase
    /// vraća sam, kroz „Moje oglase" — i tako svesno potvrđuje svaki.
    /// </summary>
    public async Task<(bool Success, string? Error)> VratiAsync(string userId)
    {
        var postoji = await db.Users.AnyAsync(u => u.Id == userId);
        if (!postoji)
            return (false, "Korisnik nije pronađen.");

        await db.Users
            .Where(u => u.Id == userId && !u.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));

        logger.LogInformation("Nalog {UserId} vraćen u rad", userId);
        return (true, null);
    }
}
