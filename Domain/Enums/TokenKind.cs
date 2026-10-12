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
    WaitlistBonus
}
