using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class Referral
{
    public int            Id             { get; set; }

    // Ko je pozvao
    public string         ReferrerId     { get; set; } = string.Empty;
    public ApplicationUser Referrer      { get; set; } = null!;

    // Ko je pozvan (unique — jedan user može biti pozvan samo jednom)
    public string         ReferredUserId { get; set; } = string.Empty;
    public ApplicationUser ReferredUser  { get; set; } = null!;

    // Kod koji je bio upotrebljen pri registraciji
    public string         ReferralCode   { get; set; } = string.Empty;

    public ReferralStatus Status         { get; set; } = ReferralStatus.Pending;
    public decimal?       TokensAwarded  { get; set; }  // null dok nije Rewarded
    public DateTime       CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime?      RewardedAt     { get; set; }
}
