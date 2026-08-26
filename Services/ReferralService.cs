using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Referral nagrada se isplaćuje u DVE rate:
///
///   1. rata — pozvani potvrdi email       → pozivalac dobija SignupRewardTokens (2)
///   2. rata — pozvani aktivira provajdera → pozivalac dobija ActivationRewardTokens (3)
///
/// Zašto prva rata ide na potvrdu emaila a ne na samu registraciju: registracija
/// je besplatna i neograničena, pa bi isplata na registraciju značila da svako
/// može da otvara naloge sa sopstvenim kodom i uzima tokene bez ikakvog rada.
/// Potvrda emaila zahteva stvarnu, jedinstvenu i funkcionalnu adresu po nalogu.
/// Uz to, nalog bez potvrđenog emaila ne može ni da se prijavi (AuthService.
/// LoginAsync ga odbija), pa u ovom sistemu ionako još nije pravi nalog.
///
/// Obe metode su IDEMPOTENTNE i bezbedne pod istovremenim pozivima. To nije
/// teorijska briga: verifikacioni link se lako aktivira dvaput (mail klijent ga
/// prefetch-uje, pa korisnik klikne), a ResetPasswordAsync je drugi put kroz
/// koji email može biti potvrđen.
/// </summary>
public sealed class ReferralService(
    AppDbContext             db,
    NotificationService      notificationService,
    IConfiguration           config,
    ILogger<ReferralService> logger)
{
    private decimal SignupRewardTokens =>
        config.GetValue<decimal>("Referral:SignupRewardTokens", 2m);

    private decimal ActivationRewardTokens =>
        config.GetValue<decimal>("Referral:ProviderActivationRewardTokens", 3m);

    // ── 1. RATA — potvrda emaila ───────────────────────────────────────────
    /// <summary>
    /// Isplaćuje prvu ratu pozivaocu korisnika koji je upravo potvrdio email.
    /// Nikad ne baca — greška u referralu ne sme oboriti potvrdu emaila.
    /// </summary>
    public async Task TryRewardSignupAsync(string referredUserId)
    {
        try
        {
            var referral = await FindPayableAsync(referredUserId);
            if (referral is null) return;

            // Prva rata pripada samo referralu koji još nije ništa dobio.
            if (referral.Status != ReferralStatus.Pending) return;

            var iznos = SignupRewardTokens;
            var sada  = DateTime.UtcNow;

            // ── Rezervacija rate ───────────────────────────────────────────
            // Uslov je u SAMOJ UPDATE naredbi, pa dva istovremena poziva ne mogu
            // oba proći: baza drži bravu na redu, drugi vidi već izmenjen status
            // i pogodi nula redova. Da je ovde stajalo `if (status == Pending)`
            // pa zaseban upis, isti pozivalac bi mogao biti plaćen dvaput.
            var rezervisano = await db.Referrals
                .Where(r => r.Id == referral.Id && r.Status == ReferralStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status,              ReferralStatus.Registered)
                    .SetProperty(r => r.SignupTokensAwarded, iznos)
                    .SetProperty(r => r.SignupRewardedAt,    sada));

            if (rezervisano == 0)
            {
                logger.LogInformation(
                    "Referral #{Id}: prva rata je već isplaćena — preskačem.", referral.Id);
                return;
            }

            await IsplatiAsync(
                referral.Id, referral.ReferrerId, iznos, sada,
                opis:   "Referral nagrada — pozvanik je potvrdio email",
                poruka: $"Vaš pozvanik je potvrdio email adresu. Nagrađeni ste sa {iznos:0.##} tokena.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Greška pri isplati prve referral rate za korisnika {UserId}", referredUserId);
        }
    }

    // ── 2. RATA — aktivacija provajdera ────────────────────────────────────
    /// <summary>
    /// Isplaćuje drugu ratu kada pozvani aktivira provajder nalog.
    ///
    /// Prihvata i status Pending, ne samo Registered: aktivacija provajdera
    /// ionako zahteva potvrđen email, ali postoje referrali iz vremena pre ove
    /// izmene koji su ostali Pending. Bez toga bi njihovi pozivaoci ostali bez
    /// druge rate.
    /// </summary>
    public async Task TryRewardActivationAsync(string referredUserId)
    {
        try
        {
            var referral = await FindPayableAsync(referredUserId);
            if (referral is null) return;

            var iznos = ActivationRewardTokens;
            var sada  = DateTime.UtcNow;

            var rezervisano = await db.Referrals
                .Where(r => r.Id == referral.Id && r.Status != ReferralStatus.Rewarded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status,                  ReferralStatus.Rewarded)
                    .SetProperty(r => r.ActivationTokensAwarded, iznos)
                    .SetProperty(r => r.ActivationRewardedAt,    sada));

            if (rezervisano == 0)
            {
                logger.LogInformation(
                    "Referral #{Id}: druga rata je već isplaćena — preskačem.", referral.Id);
                return;
            }

            await IsplatiAsync(
                referral.Id, referral.ReferrerId, iznos, sada,
                opis:   "Referral nagrada — pozvanik je aktivirao provajder nalog",
                poruka: $"Vaš pozvanik je aktivirao provajder nalog. Nagrađeni ste sa {iznos:0.##} tokena.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Greška pri isplati druge referral rate za korisnika {UserId}", referredUserId);
        }
    }

    // ── Zajedničko ─────────────────────────────────────────────────────────

    /// <summary>
    /// Referral zapis za pozvanog korisnika, ili null ako ga nema ili je sve
    /// već isplaćeno. AsNoTracking jer se izmene ionako rade kroz ExecuteUpdate.
    /// </summary>
    private async Task<Referral?> FindPayableAsync(string referredUserId)
    {
        var referral = await db.Referrals
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReferredUserId == referredUserId);

        if (referral is null) return null;                          // nije bio pozvan
        if (referral.Status == ReferralStatus.Rewarded) return null; // sve isplaćeno

        return referral;
    }

    /// <summary>
    /// Uvećava balans pozivaoca, upisuje ledger zapis i šalje obaveštenje.
    /// Poziva se tek pošto je rata uspešno rezervisana.
    /// </summary>
    private async Task IsplatiAsync(
        int referralId, string referrerId, decimal iznos, DateTime sada,
        string opis, string poruka)
    {
        // Uvećanje ide jednom naredbom (SET balance = balance + @x). Da je ovde
        // stajalo `user.TokenBalance += x`, dva referrala koja istovremeno
        // plaćaju istog pozivaoca bi se međusobno prepisala — jedna rata bi
        // nestala iz balansa, a red u ledgeru bi i dalje postojao.
        await db.Users
            .Where(u => u.Id == referrerId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.TokenBalance,
                u => u.TokenBalance + iznos));

        // ExecuteUpdateAsync zaobilazi change tracker, pa balans čitamo ponovo
        // iz baze da BalanceAfter u ledgeru bude tačan.
        var balansPosle = await db.Users
            .Where(u => u.Id == referrerId)
            .Select(u => u.TokenBalance)
            .FirstAsync();

        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = referrerId,
            Amount       = iznos,
            Kind         = TokenKind.Referral,
            ReferenceId  = referralId,
            Description  = opis,
            BalanceAfter = balansPosle,
            CreatedAt    = sada
        });

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            referrerId,
            NotificationKind.ReferralRewarded,
            "Zaradili ste tokene!",
            poruka,
            referralId);

        logger.LogInformation(
            "Referral #{Id}: isplaćeno {Amount} tokena pozivaocu {ReferrerId}",
            referralId, iznos, referrerId);
    }
}
