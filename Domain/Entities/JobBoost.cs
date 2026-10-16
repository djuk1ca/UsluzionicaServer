namespace UsluzionicaServer.Domain.Entities;

/// <summary>Jedno isticanje posla — isti oblik kao <see cref="ListingBoost"/>.</summary>
public class JobBoost
{
    public int      Id           { get; set; }
    public int      JobId        { get; set; }
    public string   UserId       { get; set; } = string.Empty;
    public decimal  TokensSpent  { get; set; }
    public int      DurationDays { get; set; }
    public DateTime StartsAt     { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt    { get; set; }

    public Job             Job  { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
