using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.Hubs;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.ChatMedia;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// JEDINO mesto koje upisuje poruku u čet i razglašava je.
///
/// Ranije je slanje postojalo dvaput — u <c>ChatHub.SendMessage</c> i u
/// <c>ConversationService.SendMessageAsync</c> (REST rezerva) — i svaka izmena
/// je morala da se dopiše na oba mesta (blokada je jednom bila samo u jednom).
/// Javljanje na posao je treći put; umesto treće kopije, sva tri idu ovuda:
/// šifrovanje, LastMessageAt, obaveštenje i SignalR.
///
/// Pozivalac je već proverio da pošiljalac pripada razgovoru i da nema blokade
/// — to zavisi od konteksta (hub, REST, posao) i ostaje kod njega.
/// </summary>
public sealed class ChatSender(
    AppDbContext            db,
    MessageEncryption       encryption,
    ChatMediaStore          medija,
    NotificationService     notifications,
    IHubContext<ChatHub>    hub,
    ILogger<ChatSender>     logger)
{
    public static string UserGroup(string userId)      => $"user-{userId}";
    public static string ConvGroup(int conversationId) => $"conversation-{conversationId}";

    /// <summary>
    /// Snima poruku, javlja primaocu (obaveštenje + push) i razglašava je kroz
    /// SignalR. <paramref name="conversation"/> mora biti PRAĆEN entitet, jer
    /// se menja njegov LastMessageAt.
    /// </summary>
    /// <param name="obavesti">
    /// false kad pozivalac šalje svoje, preciznije obaveštenje (npr. „Novo
    /// javljanje na posao" umesto „Nova poruka").
    /// </param>
    /// <param name="prilog">
    /// Slika — fajl je već upisan na disk (ChatImageService). Tekst je tada
    /// opcioni opis, a poruka je <see cref="MessageKind.Image"/>.
    /// </param>
    public async Task<MessageDto> PosaljiAsync(
        Conversation       conversation,
        ApplicationUser    sender,
        string             text,
        MessageKind        kind     = MessageKind.Text,
        JobApplication?    prijava  = null,
        bool               obavesti = true,
        MessageAttachment? prilog   = null)
    {
        if (prilog is not null) kind = MessageKind.Image;

        var receiverId = conversation.User1Id == sender.Id ? conversation.User2Id : conversation.User1Id;
        var tekst      = text.Trim();

        var message = new Message
        {
            ConversationId   = conversation.Id,
            SenderId         = sender.Id,
            Text             = encryption.Encrypt(tekst, conversation.Id),
            SentAt           = DateTime.UtcNow,
            IsRead           = false,
            Kind             = kind,
            JobApplicationId = prijava?.Id,
            Attachment       = prilog
        };

        db.Messages.Add(message);
        conversation.LastMessageAt = message.SentAt;
        await db.SaveChangesAsync();

        // Isti DTO ide obema stranama (soba razgovora), pa je URL potpisan za
        // pošiljaoca — i on je učesnik, što je jedino što endpoint proverava.
        var dto     = Mapiraj(message, sender, tekst, prijava, Slika(prilog, sender.Id));
        var pregled = PregledPoruke(kind, tekst, prijava?.Job?.Title);

        if (obavesti)
            await notifications.SendAsync(
                receiverId,
                NotificationKind.NewMessage,
                $"Nova poruka od {sender.FullName}",
                pregled,
                conversation.Id);

        // SignalR je pogodnost, ne uslov: poruka je već u bazi i stiže uz sledeće
        // učitavanje razgovora i kad razglašavanje ne uspe.
        try
        {
            await hub.Clients.Group(ConvGroup(conversation.Id)).SendAsync("ReceiveMessage", dto);

            // Primalac koji nije u sobi razgovora (lista razgovora, drugi ekran)
            // dobija ovo na ličnu grupu — da osveži listu i bedž.
            await hub.Clients.Group(UserGroup(receiverId)).SendAsync("NewInboxMessage", new
            {
                ConversationId = conversation.Id,
                SenderId       = sender.Id,
                SenderName     = sender.FullName,
                Preview        = pregled
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Razglašavanje poruke {MsgId} nije uspelo.", message.Id);
        }

        return dto;
    }

    /// <summary>
    /// Razgovor između dvoje ljudi — postojeći ili nov — kao PRAĆEN entitet.
    /// Isti redosled korisnika kao <c>ConversationService.GetOrCreateAsync</c>
    /// (leksikografski manji je User1), da UNIQUE indeks važi.
    /// </summary>
    public async Task<Conversation> RazgovorAsync(string userAId, string userBId)
    {
        var (user1Id, user2Id) = string.Compare(userAId, userBId, StringComparison.Ordinal) < 0
            ? (userAId, userBId)
            : (userBId, userAId);

        var postojeci = await db.Conversations
            .FirstOrDefaultAsync(c => c.User1Id == user1Id && c.User2Id == user2Id);
        if (postojeci is not null) return postojeci;

        var nov = new Conversation { User1Id = user1Id, User2Id = user2Id, CreatedAt = DateTime.UtcNow };
        db.Conversations.Add(nov);

        try
        {
            await db.SaveChangesAsync();
            return nov;
        }
        catch (DbUpdateException)
        {
            // Druga strana je u istom trenutku otvorila isti razgovor.
            db.Entry(nov).State = EntityState.Detached;
            return await db.Conversations.FirstAsync(c => c.User1Id == user1Id && c.User2Id == user2Id);
        }
    }

    /// <summary>Opis slike u DTO-u za gledaoca — potpisan URL, ili null kad priloga više nema.</summary>
    public ChatImageDto? Slika(MessageAttachment? a, string gledalacId) => a is null ? null : new ChatImageDto
    {
        ImageUrl    = medija.PotpisanUrl(a.Id, gledalacId),
        Width       = a.Width,
        Height      = a.Height,
        NeedsReview = a.NeedsReview
    };

    /// <summary>Stari klijenti ne znaju za slike — bez ovoga bi videli prazan balon.</summary>
    public const string RezervniTekstSlike = "📷 Fotografija — ažuriraj aplikaciju da je vidiš";

    /// <summary>Tekst pregleda za listu razgovora, obaveštenje i push.</summary>
    public static string PregledPoruke(MessageKind kind, string tekst, string? naslovPosla) => kind switch
    {
        MessageKind.Image => string.IsNullOrWhiteSpace(tekst) ? "📷 Fotografija" : $"📷 {Pregled.Skrati(tekst, 58)}",
        MessageKind.JobApplication => naslovPosla is null
            ? "Javljanje na posao"
            : $"Javljanje na posao „{Pregled.Skrati(naslovPosla, 40)}“",
        _ => Pregled.Skrati(tekst, 60)
    };

    /// <summary>
    /// DTO poruke. Za karticu javljanja <paramref name="prijava"/> treba da ima
    /// učitan posao i autora posla; bez njih kartica kaže da posao nije dostupan.
    /// </summary>
    public static MessageDto Mapiraj(
        Message m, ApplicationUser sender, string tekst, JobApplication? prijava, ChatImageDto? slika = null)
    {
        var dto = new MessageDto
        {
            Id             = m.Id,
            ConversationId = m.ConversationId,
            SenderId       = m.SenderId,
            SenderName     = sender.FullName,
            SenderImageUrl = sender.ProfileImageUrl,
            Text           = tekst,
            SentAt         = m.SentAt,
            IsRead         = m.IsRead,
            Kind           = m.Kind.ToString()
        };

        if (m.Kind == MessageKind.Image)
        {
            dto.Caption = tekst;
            dto.Image   = slika;
            dto.Text    = string.IsNullOrWhiteSpace(tekst) ? RezervniTekstSlike : $"📷 {tekst}";
        }

        if (m.Kind == MessageKind.JobApplication)
        {
            var posao = prijava?.Job;
            dto.Job = new JobCardDto
            {
                // Obrisan ili izvršen posao: kartica ostaje, ali bez linka.
                JobId       = posao is { Status: JobStatus.Open or JobStatus.Assigned or JobStatus.Expired } ? posao.Id : null,
                JobTitle    = posao?.Title ?? string.Empty,
                OfferAmount = prijava?.OfferAmount,
                AuthorName  = posao?.Author?.FullName ?? string.Empty,
                AuthorId    = posao?.AuthorId ?? string.Empty,
                JobStatus         = posao?.Status.ToString(),
                ApplicationStatus = prijava?.Status.ToString() ?? string.Empty,
                ChosenOther       = posao is { Status: JobStatus.Assigned } && posao.AssignedApplicationId != prijava!.Id
            };
        }

        return dto;
    }
}
