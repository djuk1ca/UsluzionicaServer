using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Notifications;
using UsluzionicaServer.Hubs;
using UsluzionicaServer.Infrastructure.Push;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Centralni servis za in-app notifikacije.
///
/// SendAsync: snima notifikaciju u bazu i odmah je push-uje korisniku
/// preko NotificationHub-a ("user-{userId}" SignalR grupa).
///
/// Svi ostali servisi koji triggeruju eventi (BookingService, ReviewService itd.)
/// koriste ovaj servis umesto direktnog db.Notifications.Add().
/// </summary>
public sealed class NotificationService(
    AppDbContext                       db,
    IHubContext<NotificationHub>       hubContext,
    IPushSender                        pushSender,
    ILogger<NotificationService>       logger)
{
    // ── SEND ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Snima notifikaciju u bazu i push-uje je korisniku u realnom vremenu.
    /// Ako korisnik nije online (nije konektovan na hub), notifikacija čeka u bazi
    /// i biće dostupna pri sledećem GET /api/notifications.
    /// </summary>
    public async Task SendAsync(
        string           userId,
        NotificationKind kind,
        string           title,
        string           body,
        int?             referenceId = null)
    {
        var notif = new Notification
        {
            UserId      = userId,
            Kind        = kind,
            Title       = title,
            Body        = body,
            ReferenceId = referenceId,
            IsRead      = false,
            CreatedAt   = DateTime.UtcNow
        };

        db.Notifications.Add(notif);
        await db.SaveChangesAsync();

        // SignalR push — tiho propušta grešku (korisnik možda nije online)
        try
        {
            var dto = MapToDto(notif);
            await hubContext.Clients
                .Group($"user-{userId}")
                .SendAsync("ReceiveNotification", dto);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SignalR push za notifikaciju {Id} nije uspeo (korisnik {UserId})", notif.Id, userId);
        }

        // ── PUSH NA UREĐAJE ─────────────────────────────────────────────────
        // SignalR iznad pokriva samo aplikaciju u PRVOM PLANU — konekcija umire
        // čim korisnik izađe iz nje. Push je jedini kanal koji radi kad je
        // aplikacija u pozadini ili ugašena.
        //
        // Naslov i telo se ne grade ovde nego stižu gotovi od pozivaoca, pa
        // obaveštenje na zaključanom ekranu piše isto što i lista u aplikaciji:
        // koliko tokena, ko šalje poruku i šta u njoj piše.
        //
        // FAIL-OPEN, isto pravilo kao SignalR grana. Pad FCM-a ne sme da obori
        // rezervaciju, poruku ni recenziju — notifikacija je već u bazi i
        // stići će kroz GET /api/notifications.
        try
        {
            var tokeni = await db.DeviceTokens
                .Where(t => t.UserId == userId)
                .Select(t => t.Token)
                .ToListAsync();

            if (tokeni.Count > 0)
            {
                var mrtvi = (await pushSender.SendAsync(
                    tokeni,
                    new PushMessage(notif.Id, kind.ToString(), title, body, referenceId))).ToList();

                // Tokene koje je FCM odbio kao nepostojeće brišemo odmah.
                // Bez toga tabela raste zauvek, a svako slanje čeka i na uređaje
                // kojih odavno nema — što usporava isporuku svima ostalima.
                if (mrtvi.Count > 0)
                {
                    await db.DeviceTokens
                        .Where(t => mrtvi.Contains(t.Token))
                        .ExecuteDeleteAsync();

                    logger.LogInformation(
                        "Obrisano {Count} mrtvih FCM tokena korisnika {UserId}.", mrtvi.Count, userId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Push za notifikaciju {Id} nije uspeo (korisnik {UserId})", notif.Id, userId);
        }
    }

    // ── GET ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Vraća notifikacije korisnika — nepročitane prve, zatim ostale od najnovijeg.
    /// Max 50 po pozivu.
    /// </summary>
    public async Task<List<NotificationDto>> GetAsync(string userId, int page = 1, int pageSize = 30)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);

        return await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderBy(n => n.IsRead)               // false (0) dolazi pre true (1)
            .ThenByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NotificationDto
            {
                Id          = n.Id,
                Kind        = n.Kind.ToString(),
                Title       = n.Title,
                Body        = n.Body,
                ReferenceId = n.ReferenceId,
                IsRead      = n.IsRead,
                CreatedAt   = n.CreatedAt
            })
            .ToListAsync();
    }

    /// <summary>Broj nepročitanih notifikacija — za badge na UI.</summary>
    public async Task<int> GetUnreadCountAsync(string userId) =>
        await db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

    // ── MARK READ ──────────────────────────────────────────────────────────
    /// <summary>Označi jednu notifikaciju kao pročitanu. Vraća false ako ne postoji.</summary>
    public async Task<bool> MarkReadAsync(string userId, int notificationId)
    {
        var updated = await db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

        return updated > 0;
    }

    /// <summary>Označi sve nepročitane notifikacije kao pročitane.</summary>
    public async Task MarkAllReadAsync(string userId) =>
        await db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));

    // ── UREĐAJI ────────────────────────────────────────────────────────────
    /// <summary>
    /// Prijavljuje uređaj za push, ili ga PREUZIMA od prethodnog korisnika.
    ///
    /// Nije „dodaj" nego „preuzmi", i to je suština. Jedan uređaj daje jedan FCM
    /// token — on pripada instalaciji aplikacije, ne nalogu. Kad se na istom
    /// telefonu odjavi Marko i prijavi Ana, Firebase vraća isti token.
    ///
    /// Da se ovde radio prost <c>Add</c>, jedinstveni indeks bi to odbio, a da
    /// indeksa nema, Marko bi nastavio da prima Anine poruke — sa imenom
    /// pošiljaoca i tekstom u telu obaveštenja. Zato se red pronađe po tokenu i
    /// vlasnik se prepiše.
    /// </summary>
    public async Task RegisterDeviceAsync(string userId, string token, string platform)
    {
        var sada = DateTime.UtcNow;

        var postojeci = await db.DeviceTokens.FirstOrDefaultAsync(t => t.Token == token);

        if (postojeci is not null)
        {
            postojeci.UserId     = userId;
            postojeci.Platform   = platform;
            postojeci.LastSeenAt = sada;
        }
        else
        {
            db.DeviceTokens.Add(new DeviceToken
            {
                UserId     = userId,
                Token      = token,
                Platform   = platform,
                CreatedAt  = sada,
                LastSeenAt = sada
            });
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Dva istovremena zahteva sa istim tokenom — jedan je prošao, i to
            // je jedino što je bilo važno. Klijent svakako ponavlja prijavu pri
            // svakom pokretanju.
        }
    }

    /// <summary>
    /// Odjavljuje uređaj. Zove se pri odjavi korisnika, PRE brisanja JWT-a —
    /// bez tokena zahtev ne bi prošao autorizaciju.
    ///
    /// Briše se po paru (token, korisnik): tuđi token se ne dira ni ako neko
    /// pogodi njegovu vrednost.
    /// </summary>
    public async Task UnregisterDeviceAsync(string userId, string token) =>
        await db.DeviceTokens
            .Where(t => t.Token == token && t.UserId == userId)
            .ExecuteDeleteAsync();

    // ── HELPER ─────────────────────────────────────────────────────────────
    private static NotificationDto MapToDto(Notification n) => new()
    {
        Id          = n.Id,
        Kind        = n.Kind.ToString(),
        Title       = n.Title,
        Body        = n.Body,
        ReferenceId = n.ReferenceId,
        IsRead      = n.IsRead,
        CreatedAt   = n.CreatedAt
    };
}
