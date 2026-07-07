using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class DiscountTokenOffer
{
    public int                  Id             { get; set; }
    public string               SenderId       { get; set; } = string.Empty;
    public string               ReceiverId     { get; set; } = string.Empty;
    public int                  ListingId      { get; set; }
    public int?                 ConversationId { get; set; }
    public decimal              TokenAmount    { get; set; }
    public DiscountOfferStatus  Status         { get; set; } = DiscountOfferStatus.Pending;
    public DateTime             CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime?            RespondedAt    { get; set; }

    // Navigation
    public ApplicationUser Sender       { get; set; } = null!;
    public ApplicationUser Receiver     { get; set; } = null!;
    public Listing         Listing      { get; set; } = null!;
    public Conversation?   Conversation { get; set; }
}
