using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class Message
{
    public int      Id             { get; set; }
    public int      ConversationId { get; set; }
    public string   SenderId       { get; set; } = string.Empty;
    public string   Text           { get; set; } = string.Empty;
    public DateTime SentAt         { get; set; } = DateTime.UtcNow;
    public bool     IsRead         { get; set; } = false;

    /// <summary>Obična poruka ili kartica (javljanje na posao…) — vidi <see cref="MessageKind"/>.</summary>
    public MessageKind Kind        { get; set; } = MessageKind.Text;

    /// <summary>
    /// Za <see cref="MessageKind.JobApplication"/>: javljanje koje kartica
    /// prikazuje. Null kad je posao u međuvremenu trajno obrisan — kartica tada
    /// piše „Posao više nije dostupan".
    /// </summary>
    public int?     JobApplicationId { get; set; }

    // Navigation
    public Conversation    Conversation   { get; set; } = null!;
    public ApplicationUser Sender         { get; set; } = null!;
    public JobApplication? JobApplication { get; set; }

    /// <summary>Slika — samo za <see cref="MessageKind.Image"/>.</summary>
    public MessageAttachment? Attachment  { get; set; }
}
