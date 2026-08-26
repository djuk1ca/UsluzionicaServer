namespace UsluzionicaServer.DTOs.Referrals;

/// <summary>Sopstveni referral kod sa shareable linkom za deljenje.</summary>
public sealed class MyReferralCodeDto
{
    public string ReferralCode  { get; set; } = string.Empty;

    /// <summary>Pun link spreman za kopiranje i slanje: {baseUrl}/register?ref={code}</summary>
    public string ShareableLink { get; set; } = string.Empty;
}

/// <summary>Statistika referral programa za prijavljenog korisnika.</summary>
public sealed class ReferralStatsDto
{
    public int     TotalInvited         { get; set; }  // ukupno pozvano
    public int     TotalBecameProvider  { get; set; }  // postali provajderi (Rewarded)
    public int     TotalPending         { get; set; }  // pozvani ali još nisu provajderi
    public decimal TotalTokensEarned    { get; set; }  // ukupno zarađeni tokeni

    public List<ReferralEntryDto> Referrals { get; set; } = [];
}

/// <summary>Jedan red u listi pozvanika.</summary>
public sealed class ReferralEntryDto
{
    public string    InviteeName  { get; set; } = string.Empty;

    /// <summary>Pending (nije potvrdio email) | Registered (jeste) | Rewarded (postao provajder)</summary>
    public string    Status       { get; set; } = string.Empty;

    public DateTime  InvitedAt    { get; set; }

    /// <summary>Kada je isplaćena 1. rata — null dok pozvani ne potvrdi email.</summary>
    public DateTime? SignupRewardedAt { get; set; }

    /// <summary>Kada je isplaćena 2. rata — null dok pozvani ne aktivira provajder nalog.</summary>
    public DateTime? RewardedAt   { get; set; }

    /// <summary>1. rata (potvrda emaila). Null dok nije isplaćena.</summary>
    public decimal?  SignupTokens     { get; set; }

    /// <summary>2. rata (aktivacija provajdera). Null dok nije isplaćena.</summary>
    public decimal?  ActivationTokens { get; set; }

    /// <summary>Zbir obe rate — 0 dok ništa nije isplaćeno.</summary>
    public decimal   TokensEarned { get; set; }
}
