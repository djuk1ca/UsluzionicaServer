using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Notifications;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(NotificationService notificationService) : ControllerBase
{
    // ── GET /api/notifications ─────────────────────────────────────────────
    /// <summary>
    /// Lista notifikacija prijavljenog korisnika.
    /// Nepročitane dolaze prve, zatim ostale od najnovijeg.
    /// Query: ?page=1&amp;pageSize=30
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<NotificationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 30)
    {
        var userId        = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var notifications = await notificationService.GetAsync(userId, page, pageSize);
        var unreadCount   = await notificationService.GetUnreadCountAsync(userId);

        return Ok(new
        {
            success     = true,
            data        = notifications,
            total       = notifications.Count,
            unreadCount = unreadCount
        });
    }

    // ── PATCH /api/notifications/{id}/read ────────────────────────────────
    /// <summary>Označi jednu notifikaciju kao pročitanu.</summary>
    [HttpPatch("{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(int id)
    {
        var userId  = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var updated = await notificationService.MarkReadAsync(userId, id);

        if (!updated)
            return NotFound(new { success = false, message = "Notifikacija nije pronađena." });

        return Ok(new { success = true });
    }

    // ── PATCH /api/notifications/read-all ────────────────────────────────
    /// <summary>Označi sve nepročitane notifikacije kao pročitane.</summary>
    [HttpPatch("read-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await notificationService.MarkAllReadAsync(userId);
        return Ok(new { success = true });
    }
}
