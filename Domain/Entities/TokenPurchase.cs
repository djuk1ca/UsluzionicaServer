using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class TokenPurchase
{
    public int                 Id            { get; set; }
    public string              UserId        { get; set; } = string.Empty;
    public decimal             Tokens        { get; set; }
    public decimal             BonusTokens   { get; set; } = 0m;
    public decimal             AmountRsd     { get; set; }
    public string              PaymentMethod { get; set; } = string.Empty;
    public TokenPurchaseStatus Status        { get; set; } = TokenPurchaseStatus.Pending;
    public DateTime            CreatedAt     { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
}
