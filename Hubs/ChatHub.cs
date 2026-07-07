using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Hubs;

/// <summary>
/// SignalR hub za real-time chat.
/// Konekcija: wss://host/hubs/chat?access_token=JWT
///
/// Svaki korisnik pri konektovanju automatski ulazi u grupu "user-{userId}".
/// Kada se pridruži konverzaciji (JoinConversation), ulazi i u grupu "conversation-{id}".
/// </summary>
[Authorize]
public sealed class ChatHub(
    AppDbContext        db,
    MessageEncryption   encryption,
    OnlineTracker       tracker,
    NotificationService notificationService,
    ILogger<ChatHub>    logger) : Hub
{
    // ── KONEKCIJA ──────────────────────────────────────────────────────────
    public override async Task OnConnectedAsync()
    {
        var userId = UserId();

        // Registruj konekciju u OnlineTracker
        tracker.Register(userId, Context.ConnectionId);

        // Korisnik ulazi u svoju ličnu grupu (za direktne poruke servera)
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));

        // Obavesti sve koji imaju otvorenu konverzaciju sa ovim korisnikom
        // da je on online. Šaljemo im "UserOnlineStatus" event.
        var contactIds = await GetContactIdsAsync(userId);
        foreach (var contactId in contactIds)
        {
            await Clients
                .Group(UserGroup(contactId))
                .SendAsync("UserOnlineStatus", userId, true);
        }

        logger.LogDebug("Korisnik konektovan: {UserId} ({ConnectionId})", userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    // ── DISKONEKCIJA ───────────────────────────────────────────────────────
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId    = UserId();
        var fullyGone = tracker.Unregister(userId, Context.ConnectionId);

        // Samo ako je korisnik POTPUNO offline (nema više ni jedne konekcije)
        // obaveštavamo kontakte.
        if (fullyGone)
        {
            var contactIds = await GetContactIdsAsync(userId);
            foreach (var contactId in contactIds)
            {
                await Clients
                    .Group(UserGroup(contactId))
                    .SendAsync("UserOnlineStatus", userId, false);
            }
        }

        logger.LogDebug("Korisnik diskonektovan: {UserId} ({ConnectionId}), potpunoOffline={Gone}",
            userId, Context.ConnectionId, fullyGone);

        await base.OnDisconnectedAsync(exception);
    }

    // ── JOIN CONVERSATION ──────────────────────────────────────────────────
    /// <summary>
    /// Klijent poziva ovo da bi "ušao" u sobu konverzacije.
    /// Nakon toga prima ReceiveMessage evente za tu konverzaciju.
    /// Validira da korisnik zaista pripada toj konverzaciji.
    /// </summary>
    public async Task JoinConversation(int conversationId)
    {
        var userId = UserId();

        var isMember = await db.Conversations.AnyAsync(c =>
            c.Id == conversationId &&
            (c.User1Id == userId || c.User2Id == userId));

        if (!isMember)
        {
            logger.LogWarning(
                "Korisnik {UserId} pokušao da uđe u konverzaciju {CId} kojoj ne pripada",
                userId, conversationId);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, ConvGroup(conversationId));

        logger.LogDebug("Korisnik {UserId} ušao u konverzaciju {CId}", userId, conversationId);
    }

    // ── SEND MESSAGE ───────────────────────────────────────────────────────
    /// <summary>
    /// Klijent šalje poruku. Server:
    ///   1. Validira i enkriptuje tekst
    ///   2. Snima u bazu
    ///   3. Ažurira LastMessageAt na konverzaciji
    ///   4. Kreira in-app notifikaciju za primaoca
    ///   5. Broadcast-uje ReceiveMessage svima u sobi konverzacije
    ///   6. Vraća potvrdu pošiljaocu (Caller.SendAsync "MessageSent")
    /// </summary>
    public async Task SendMessage(int conversationId, string text)
    {
        var senderId = UserId();

        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000)
        {
            await Clients.Caller.SendAsync("Error", "Poruka mora biti između 1 i 4000 znakova.");
            return;
        }

        // Proveri konverzaciju i pronađi primaoca
        var conv = await db.Conversations
            .Include(c => c.User1)
            .Include(c => c.User2)
            .FirstOrDefaultAsync(c =>
                c.Id == conversationId &&
                (c.User1Id == senderId || c.User2Id == senderId));

        if (conv is null)
        {
            await Clients.Caller.SendAsync("Error", "Konverzacija nije pronađena.");
            return;
        }

        var sender     = conv.User1Id == senderId ? conv.User1 : conv.User2;
        var receiverId = conv.User1Id == senderId ? conv.User2Id : conv.User1Id;

        // Enkriptuj pre snimanja u bazu
        var encrypted = encryption.Encrypt(text.Trim());

        var message = new Message
        {
            ConversationId = conversationId,
            SenderId       = senderId,
            Text           = encrypted,
            SentAt         = DateTime.UtcNow,
            IsRead         = false
        };

        db.Messages.Add(message);
        conv.LastMessageAt = message.SentAt;

        var preview = text.Length > 60 ? text[..60] + "…" : text;

        await db.SaveChangesAsync();

        // In-app notifikacija (kratak preview, nešifrovano) + SignalR push
        await notificationService.SendAsync(
            receiverId,
            NotificationKind.NewMessage,
            $"Nova poruka od {sender.FullName}",
            preview,
            conversationId);

        // DTO sa DEKRIPTOVANIM tekstom — klijenti nikad ne vide šifrovani tekst
        var dto = new MessageDto
        {
            Id             = message.Id,
            ConversationId = conversationId,
            SenderId       = senderId,
            SenderName     = sender.FullName,
            SenderImageUrl = sender.ProfileImageUrl,
            Text           = text.Trim(), // originalni tekst (već imamo ga, ne trebamo decrypt)
            SentAt         = message.SentAt,
            IsRead         = false
        };

        // Broadcast svima u sobi konverzacije (uključuje i pošiljaoca ako ga ima)
        await Clients
            .Group(ConvGroup(conversationId))
            .SendAsync("ReceiveMessage", dto);

        // Ako primalac NIJE u sobi konverzacije (možda ima hub otvoren ali nije joinovao),
        // pošalji mu na ličnu grupu — da može ažurirati inbox
        await Clients
            .Group(UserGroup(receiverId))
            .SendAsync("NewInboxMessage", new
            {
                ConversationId = conversationId,
                SenderId       = senderId,
                SenderName     = sender.FullName,
                Preview        = preview
            });

        logger.LogDebug(
            "Poruka {MsgId} poslata u konverzaciji {CId} od {SenderId}",
            message.Id, conversationId, senderId);
    }

    // ── TYPING INDICATOR ───────────────────────────────────────────────────
    /// <summary>
    /// Kada korisnik kuca, emituje "TypingIndicator" ostalima u konverzaciji.
    /// Klijent treba da poziva ovo svake ~2 sekunde dok korisnik kuca.
    /// </summary>
    public async Task UserTyping(int conversationId)
    {
        var userId = UserId();

        // Broadcast svima OSIM pošiljaocu
        await Clients
            .OthersInGroup(ConvGroup(conversationId))
            .SendAsync("TypingIndicator", userId);
    }

    // ── HELPERS ────────────────────────────────────────────────────────────
    private string UserId() =>
        Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("Korisnik nije autentifikovan.");

    private static string UserGroup(string userId)       => $"user-{userId}";
    private static string ConvGroup(int conversationId)  => $"conversation-{conversationId}";

    /// <summary>
    /// Vraća userId-ove svih korisnika sa kojima ovaj korisnik ima konverzaciju.
    /// Koristi se za broadcast online/offline statusa.
    /// </summary>
    private async Task<List<string>> GetContactIdsAsync(string userId)
    {
        return await db.Conversations
            .AsNoTracking()
            .Where(c => c.User1Id == userId || c.User2Id == userId)
            .Select(c => c.User1Id == userId ? c.User2Id : c.User1Id)
            .Distinct()
            .ToListAsync();
    }
}
