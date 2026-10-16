namespace UsluzionicaServer.Domain.Enums;

public enum TokenKind
{
    ServiceReward,
    Purchase,
    BoostSpend,
    DiscountSent,
    DiscountReceived,
    Referral,
    AdminGrant,

    /// <summary>10 tokena za upis na listu čekanja pre izlaska (WaitlistBonus).</summary>
    WaitlistBonus,

    /// <summary>Nagrada za izvršen posao (autor 3, izvršilac 5). Referenca je posao.</summary>
    JobReward,

    /// <summary>Isticanje posla. Referenca je posao.</summary>
    JobBoostSpend
}
