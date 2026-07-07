using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace UsluzionicaServer.Hubs;

/// <summary>
/// SignalR hub za in-app notifikacije.
/// Konekcija: wss://host/hubs/notifications?access_token=JWT
///
/// Klijent se konektuje → automatski ulazi u grupu "user-{userId}".
/// Server šalje "ReceiveNotification" event sa NotificationDto kada nastane događaj.
/// Nema metoda koje klijent poziva — sav tok je server → klijent.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new HubException("Korisnik nije autentifikovan.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
        await base.OnConnectedAsync();
    }
}
