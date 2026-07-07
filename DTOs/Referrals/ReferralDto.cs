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

    /// <summary>Pending | Rewarded</summary>
    public string    Status       { get; set; } = string.Empty;

    public DateTime  InvitedAt    { get; set; }
    public DateTime? RewardedAt   { get; set; }

    /// <summary>Null dok korisnik nije aktivirao provider nalog.</summary>
    public decimal?  TokensEarned { get; set; }
}
