using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Conversations;

/// <summary>Jedna stavka u listi konverzacija korisnika.</summary>
public sealed class ConversationDto
{
    public int      Id                { get; set; }

    // Drugi korisnik u konverzaciji (ne onaj koji gleda)
    public string   OtherUserId       { get; set; } = string.Empty;
    public string   OtherUserName     { get; set; } = string.Empty;
    public string?  OtherUserImageUrl { get; set; }
    public bool     OtherUserIsOnline { get; set; }

    // Poslednja poruka (tekst skraćen na 80 znakova, za preview)
    public string?  LastMessagePreview { get; set; }
    public DateTime? LastMessageAt     { get; set; }

    // Broj nepročitanih poruka za trenutnog korisnika
    public int      UnreadCount        { get; set; }

    public DateTime CreatedAt          { get; set; }
}

/// <summary>Jedna poruka unutar konverzacije — tekst je već dekriptovan.</summary>
public sealed class MessageDto
{
    public int      Id             { get; set; }
    public int      ConversationId { get; set; }
    public string   SenderId       { get; set; } = string.Empty;
    public string   SenderName     { get; set; } = string.Empty;
    public string?  SenderImageUrl { get; set; }
    public string   Text           { get; set; } = string.Empty; // uvek dekriptovan
    public DateTime SentAt         { get; set; }
    public bool     IsRead         { get; set; }
}

/// <summary>Body za kreiranje nove konverzacije.</summary>
public sealed class CreateConversationDto
{
    [Required]
    public string ReceiverId { get; set; } = string.Empty;
}

/// <summary>Body za slanje poruke putem REST-a (fallback od SignalR-a).</summary>
public sealed class SendMessageDto
{
    [Required, MaxLength(4000)]
    public string Text { get; set; } = string.Empty;
}
