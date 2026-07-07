namespace UsluzionicaServer.Domain.Entities;

public class Conversation
{
    public int       Id            { get; set; }
    public string    User1Id       { get; set; } = string.Empty;
    public string    User2Id       { get; set; } = string.Empty;
    public DateTime  CreatedAt     { get; set; } = DateTime.UtcNow;
    public DateTime? LastMessageAt { get; set; }

    // Navigation
    public ApplicationUser         User1    { get; set; } = null!;
    public ApplicationUser         User2    { get; set; } = null!;
    public ICollection<Message>    Messages { get; set; } = [];
    public ICollection<DiscountTokenOffer> DiscountOffers { get; set; } = [];
}
