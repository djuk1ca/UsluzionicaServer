using System.ComponentModel.DataAnnotations.Schema;
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
    public DateTime       CreatedAt      { get; set; } = DateTime.UtcNow;

    // ── Prva rata: pozvani je potvrdio email ───────────────────────────────
    // null dok nije isplaćeno. Namerno nullable, a ne 0: razlikuje
    // "nije isplaćeno" od "isplaćeno nula".
    public decimal?       SignupTokensAwarded { get; set; }
    public DateTime?      SignupRewardedAt    { get; set; }

    // ── Druga rata: pozvani je aktivirao provajder nalog ───────────────────
    public decimal?       ActivationTokensAwarded { get; set; }
    public DateTime?      ActivationRewardedAt    { get; set; }

    /// <summary>Ukupno isplaćeno po ovom referralu. Nije kolona — računa se.</summary>
    [NotMapped]
    public decimal TotalTokensAwarded =>
        (SignupTokensAwarded ?? 0m) + (ActivationTokensAwarded ?? 0m);
}
