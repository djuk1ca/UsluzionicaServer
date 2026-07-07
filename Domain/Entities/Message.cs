namespace UsluzionicaServer.Domain.Entities;

public class Message
{
    public int      Id             { get; set; }
    public int      ConversationId { get; set; }
    public string   SenderId       { get; set; } = string.Empty;
    public string   Text           { get; set; } = string.Empty;
    public DateTime SentAt         { get; set; } = DateTime.UtcNow;
    public bool     IsRead         { get; set; } = false;

    // Navigation
    public Conversation    Conversation { get; set; } = null!;
    public ApplicationUser Sender       { get; set; } = null!;
}
