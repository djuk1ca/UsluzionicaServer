namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Referral se isplaćuje u DVE rate, pa status prati koja je poslednja stigla.
///
///   Pending    → pozvani se registrovao, ali još nije potvrdio email.
///                Nijedan token nije isplaćen.
///   Registered → pozvani je potvrdio email → pozivalac je dobio prvu ratu.
///   Rewarded   → pozvani je aktivirao provajder nalog → isplaćena i druga rata.
///
/// Zašto prva rata ide tek na POTVRDU EMAILA, a ne na samu registraciju:
/// registracija je besplatna i neograničena, pa bi isplata na registraciju
/// značila da svako može da otvara naloge sa svojim kodom i uzima tokene.
/// Potvrda emaila zahteva stvarnu, jedinstvenu, funkcionalnu adresu po nalogu.
/// Uz to, nalog bez potvrđenog emaila ne može ni da se prijavi (vidi
/// AuthService.LoginAsync), pa u ovom sistemu ionako još nije "nalog".
/// </summary>
public enum ReferralStatus
{
    Pending,
    Registered,
    Rewarded
}
