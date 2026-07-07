using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Notifications;
using UsluzionicaServer.Hubs;
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
