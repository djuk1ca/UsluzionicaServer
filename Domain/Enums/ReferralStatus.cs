namespace UsluzionicaServer.Domain.Enums;

public enum ReferralStatus
{
    Pending,    // korisnik se registrovao, ali još nije aktivirao provider profil
    Rewarded    // provider profil aktiviran → tokeni dodeljeni referreru
}
