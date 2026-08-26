using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class ConversationService(
    AppDbContext                 db,
    UserManager<ApplicationUser> userManager,
    MessageEncryption            encryption,
    OnlineTracker                tracker,
    NotificationService          notificationService,
    ILogger<ConversationService> logger)
{
    // ── LISTA KONVERZACIJA ─────────────────────────────────────────────────
    /// <summary>
    /// Vraća sve konverzacije prijavljenog korisnika, sortirane od najnovije.
    /// Prikazuje "drugog" korisnika, preview zadnje poruke i broj nepročitanih.
    /// </summary>
    public async Task<List<ConversationDto>> GetConversationsAsync(string userId)
    {
        var conversations = await db.Conversations
            .AsNoTracking()
            .Include(c => c.User1)
            .Include(c => c.User2)
            .Include(c => c.Messages.OrderByDescending(m => m.SentAt).Take(1))
            .Where(c => c.User1Id == userId || c.User2Id == userId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync();

        // Online status za SVE sagovornike odjednom.
        // Ranije je `tracker.IsOnline(...)` pozivan unutar petlje; sa Redis-om
        // bi to bilo N odvojenih mrežnih obilazaka po otvaranju liste.
        // Jedan batch poziv umesto toga šalje sve upite kroz isti pipeline.
        var onlineIds = await tracker.WhoIsOnlineAsync(
            conversations.Select(c => c.User1Id == userId ? c.User2Id : c.User1Id));

        var result = new List<ConversationDto>();

        foreach (var c in conversations)
        {
            // "Drugi" korisnik — ne onaj koji gleda listu
            var other = c.User1Id == userId ? c.User2 : c.User1;

            // Preview zadnje poruke (dekriptovana, skraćena)
            string? preview = null;
            var lastMsg = c.Messages.FirstOrDefault();
            if (lastMsg is not null)
            {
                var decrypted = encryption.SafeDecrypt(lastMsg.Text);
                preview = decrypted.Length > 80
                    ? decrypted[..80] + "…"
                    : decrypted;
            }

            // Broj nepročitanih poruka KOJE JE DRUGI KORISNIK POSLAO (a meni nisu pročitane)
            var unread = await db.Messages
                .CountAsync(m =>
                    m.ConversationId == c.Id &&
                    m.SenderId       != userId &&
                    !m.IsRead);

            result.Add(new ConversationDto
            {
                Id                = c.Id,
                OtherUserId       = other.Id,
                OtherUserName     = other.FullName,
                OtherUserImageUrl = other.ProfileImageUrl,
                OtherUserIsOnline = onlineIds.Contains(other.Id),
                LastMessagePreview = preview,
                LastMessageAt      = c.LastMessageAt,
                UnreadCount        = unread,
                CreatedAt          = c.CreatedAt
            });
        }

        return result;
    }

    // ── KREIRANJE ILI PRONALAZAK KONVERZACIJE ──────────────────────────────
    /// <summary>
    /// Kreira konverzaciju između dva korisnika ako ne postoji.
    /// Ako već postoji → vraća tu istu.
    ///
    /// User1Id/User2Id se normalizuju (manji ID ide na User1) kako bi
    /// UNIQUE index na (User1Id, User2Id) radio ispravno bez obzira
    /// ko inicira razgovor.
    /// </summary>
    public async Task<(ConversationDto? Result, string? Error)> GetOrCreateAsync(
        string requesterId, string receiverId)
    {
        if (requesterId == receiverId)
            return (null, "Ne možeš otvoriti razgovor sam sa sobom.");

        var receiver = await userManager.FindByIdAsync(receiverId);
        if (receiver is null)
            return (null, "Korisnik nije pronađen.");

        // Normalizacija: leksikografski manji ID uvek ide kao User1
        var (user1Id, user2Id) = string.Compare(requesterId, receiverId, StringComparison.Ordinal) < 0
            ? (requesterId, receiverId)
            : (receiverId, requesterId);

        // Pronađi ili kreiraj
        var existing = await db.Conversations
            .FirstOrDefaultAsync(c => c.User1Id == user1Id && c.User2Id == user2Id);

        if (existing is not null)
            return (await ToConversationDtoAsync(existing, requesterId), null);

        var conversation = new Conversation
        {
            User1Id   = user1Id,
            User2Id   = user2Id,
            CreatedAt = DateTime.UtcNow
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Nova konverzacija kreirana: id={Id} između {U1} i {U2}",
            conversation.Id, user1Id, user2Id);

        return (await ToConversationDtoAsync(conversation, requesterId), null);
    }

    // ── ISTORIJA PORUKA ────────────────────────────────────────────────────
    /// <summary>
    /// Vraća paginovanu istoriju poruka, sortirane od NAJSTARIJE (hronološki).
    /// Svaki tekst je dekriptovan pre vraćanja klijentu.
    /// </summary>
    public async Task<(List<MessageDto>? Messages, string? Error)> GetMessagesAsync(
        int conversationId, string userId, int page = 1, int pageSize = 50)
    {
        // Proveri da korisnik pripada ovoj konverzaciji
        var conv = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId &&
                                      (c.User1Id == userId || c.User2Id == userId));
        if (conv is null)
            return (null, "Konverzacija nije pronađena.");

        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(page, 1);

        var messages = await db.Messages
            .AsNoTracking()
            .Include(m => m.Sender)
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.SentAt)  // najstarije prve (hronološki chat)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (messages.Select(m => new MessageDto
        {
            Id             = m.Id,
            ConversationId = m.ConversationId,
            SenderId       = m.SenderId,
            SenderName     = m.Sender.FullName,
            SenderImageUrl = m.Sender.ProfileImageUrl,
            Text           = encryption.SafeDecrypt(m.Text), // dekriptovano
            SentAt         = m.SentAt,
            IsRead         = m.IsRead
        }).ToList(), null);
    }

    // ── SLANJE PORUKE (REST fallback) ──────────────────────────────────────
    /// <summary>
    /// REST verzija slanja poruke — koristiti ako SignalR nije dostupan.
    /// Enkriptuje tekst, snima u bazu, vraća DTO sa dekriptovanim tekstom.
    /// </summary>
    public async Task<(MessageDto? Message, string? Error)> SendMessageAsync(
        int conversationId, string senderId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, "Poruka ne sme biti prazna.");
        if (text.Length > 4000)
            return (null, "Poruka ne sme biti duža od 4000 znakova.");

        var conv = await db.Conversations
            .Include(c => c.User1)
            .Include(c => c.User2)
            .FirstOrDefaultAsync(c => c.Id == conversationId &&
                                      (c.User1Id == senderId || c.User2Id == senderId));
        if (conv is null)
            return (null, "Konverzacija nije pronađena.");

        var sender = await userManager.FindByIdAsync(senderId);
        if (sender is null) return (null, "Korisnik nije pronađen.");

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

        var receiverId = conv.User1Id == senderId ? conv.User2Id : conv.User1Id;
        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            receiverId,
            NotificationKind.NewMessage,
            $"Nova poruka od {sender.FullName}",
            text.Length > 60 ? text[..60] + "…" : text,
            conversationId);

        return (new MessageDto
        {
            Id             = message.Id,
            ConversationId = conversationId,
            SenderId       = senderId,
            SenderName     = sender.FullName,
            SenderImageUrl = sender.ProfileImageUrl,
            Text           = text, // originalni, nešifrovani tekst
            SentAt         = message.SentAt,
            IsRead         = false
        }, null);
    }

    // ── OZNAČI KAO PROČITANO ───────────────────────────────────────────────
    /// <summary>
    /// Označava sve poruke u konverzaciji kao pročitane za trenutnog korisnika.
    /// Ažurira samo poruke koje su DRUGI korisnik poslao (ne sopstvene).
    /// </summary>
    public async Task<(bool Success, string? Error)> MarkAsReadAsync(
        int conversationId, string userId)
    {
        var conv = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId &&
                                      (c.User1Id == userId || c.User2Id == userId));
        if (conv is null)
            return (false, "Konverzacija nije pronađena.");

        await db.Messages
            .Where(m =>
                m.ConversationId == conversationId &&
                m.SenderId       != userId && // poruke koje sam PRIMIO (ne poslao)
                !m.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true));

        return (true, null);
    }

    // ── HELPER ─────────────────────────────────────────────────────────────
    private async Task<ConversationDto> ToConversationDtoAsync(
        Conversation c, string viewerId)
    {
        // Učitaj korisnike ako nisu učitani
        c.User1 ??= (await userManager.FindByIdAsync(c.User1Id))!;
        c.User2 ??= (await userManager.FindByIdAsync(c.User2Id))!;

        var other = c.User1Id == viewerId ? c.User2 : c.User1;

        return new ConversationDto
        {
            Id                = c.Id,
            OtherUserId       = other.Id,
            OtherUserName     = other.FullName,
            OtherUserImageUrl = other.ProfileImageUrl,
            OtherUserIsOnline = await tracker.IsOnlineAsync(other.Id),
            LastMessagePreview = null,
            LastMessageAt      = c.LastMessageAt,
            UnreadCount        = 0,
            CreatedAt          = c.CreatedAt
        };
    }
}
