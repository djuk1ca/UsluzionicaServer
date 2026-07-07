using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class TokenTransaction
{
    public int        Id           { get; set; }
    public string     UserId       { get; set; } = string.Empty;
    public decimal    Amount       { get; set; }
    public TokenKind  Kind         { get; set; }
    public int?       ReferenceId  { get; set; }
    public string     Description  { get; set; } = string.Empty;
    public decimal    BalanceAfter { get; set; }
    public DateTime   CreatedAt    { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
}
