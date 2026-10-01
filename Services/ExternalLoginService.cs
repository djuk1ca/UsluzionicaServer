using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.ExternalAuth;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Pretvara identitet sa Google-a ili Facebook-a u nalog u Uslužionici.
///
/// Jedno dugme služi i za prijavu i za registraciju — korisnik ne mora da zna
/// da li već ima nalog. Redosled odlučivanja:
///
///   1. Veza (provajder + id) već postoji          → prijava
///   2. Nalog sa istim mejlom postoji, mejl potvrđen → poveži, pa prijava
///      kod provajdera
///   3. Nalog sa istim mejlom postoji, a provajder  → odbij, uz poruku kojim
///      ne garantuje mejl (Facebook)                  načinom da se prijavi
///   4. Ništa od toga                              → „Dovrši nalog"
/// </summary>
public sealed class ExternalLoginService(
    UserManager<ApplicationUser>   userManager,
    AppDbContext                   db,
    AuthService                    authService,
    ReferralService                referralService,
    ILogger<ExternalLoginService>  logger)
{
    /// <summary>
    /// Kodovi grešaka koji putuju u aplikaciju kroz povratnu adresu.
    /// Kod, a ne tekst: adresa nije mesto za rečenice sa dijakritikom, a
    /// aplikacija ih prevodi u poruke na jednom mestu.
    /// </summary>
    public static class Greske
    {
        public const string Otkazano     = "otkazano";
        public const string NemaEmail    = "nema-email";
        public const string Deaktiviran  = "deaktiviran";
        public const string NalogPostoji = "nalog-postoji";
        public const string NijePodeseno = "nije-podeseno";
        public const string Opsta        = "greska";
    }

    public abstract record Ishod;
    public sealed record Prijavljen(ApplicationUser User) : Ishod;
    public sealed record TrebaRegistracija : Ishod;
    public sealed record Odbijen(string Greska) : Ishod;

    // ── Posle povratka sa Google-a / Facebook-a ────────────────────────────
    public async Task<Ishod> RazresiAsync(ExternalIdentity id)
    {
        // 1. Već povezan nalog. Mejl se ovde NE gleda: korisnik je mogao da ga
        //    promeni kod provajdera, a nalog mora ostati isti.
        var povezan = await userManager.FindByLoginAsync(id.Provider, id.ProviderKey);
        if (povezan is not null)
            return povezan.IsActive ? new Prijavljen(povezan) : new Odbijen(Greske.Deaktiviran);

        // Bez mejla nema ni povezivanja ni naloga — Identity ga traži jedinstvenog,
        // a bez njega korisnik ne bi mogao ni da resetuje pristup.
        // (Facebook nalog registrovan samo brojem telefona, ili korisnik koji je
        // na Facebook-ovom ekranu odbio da podeli mejl.)
        if (string.IsNullOrWhiteSpace(id.Email))
            return new Odbijen(Greske.NemaEmail);

        var postojeci = await userManager.FindByEmailAsync(id.Email);
        if (postojeci is null)
            return new TrebaRegistracija();

        if (!postojeci.IsActive)
            return new Odbijen(Greske.Deaktiviran);

        // 3. Vidi komentar uz EmailVerified u FacebookAuthProvider.
        if (!id.EmailVerified)
        {
            logger.LogInformation(
                "{Provider} prijava odbijena: nalog sa istim mejlom već postoji, a provajder ne garantuje mejl.",
                id.Provider);
            return new Odbijen(Greske.NalogPostoji);
        }

        await PoveziAsync(postojeci, id);
        return new Prijavljen(postojeci);
    }

    // ── „Dovrši nalog" ─────────────────────────────────────────────────────
    public async Task<(ApplicationUser? User, string? Error)> RegistrujAsync(
        ExternalIdentity id, ExternalRegisterRequest req, string? referralIzPrijave)
    {
        if (!req.AcceptedPolicy)
            return (null, "Morate prihvatiti politiku privatnosti.");

        // Idempotentno. Karta za registraciju važi 30 minuta i nije jednokratna,
        // pa dupli tap na „Napravi nalog" ne sme da napravi dva naloga ni da
        // vrati grešku za nalog koji je upravo uspešno napravljen.
        var vec = await userManager.FindByLoginAsync(id.Provider, id.ProviderKey);
        if (vec is not null)
            return vec.IsActive ? (vec, null) : (null, "Nalog je deaktiviran. Kontaktiraj podršku.");

        if (string.IsNullOrWhiteSpace(id.Email))
            return (null, "Nalog bez email adrese nije moguć.");

        // Neko je u međuvremenu napravio nalog sa istim mejlom (drugi uređaj,
        // registracija lozinkom). Ne povezuje se ovde — isto pravilo kao gore.
        if (await userManager.FindByEmailAsync(id.Email) is not null)
            return (null, "Nalog sa ovom email adresom već postoji. Prijavi se načinom kojim si ga napravio.");

        string referralCode;
        do { referralCode = TokenService.GenerateReferralCode(); }
        while (await db.Users.AnyAsync(u => u.ReferralCode == referralCode));

        var user = new ApplicationUser
        {
            UserName      = id.Email,
            Email         = id.Email,
            FullName      = req.FullName.Trim(),
            LastKnownCity = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim(),
            ReferralCode  = referralCode,
            IsActive      = true,

            // Mejl je potvrdio provajder; verifikacioni link bi bio samo
            // prepreka bez ikakve nove informacije.
            EmailConfirmed = true,

            PolicyAcceptedAt      = DateTime.UtcNow,
            PolicyVersionAccepted = PolicyVersion.Current
        };

        // BEZ LOZINKE. Nalog napravljen preko Google-a nema lozinku dok je
        // korisnik sam ne postavi kroz „Zaboravljena lozinka" — nasumična
        // lozinka koju niko ne zna bila bi samo još jedna stvar za napad.
        var created = await userManager.CreateAsync(user);
        if (!created.Succeeded)
            return (null, string.Join(" ", created.Errors.Select(e => e.Description)));

        await userManager.AddToRoleAsync(user, "User");

        var linked = await userManager.AddLoginAsync(
            user, new UserLoginInfo(id.Provider, id.ProviderKey, id.Provider));

        if (!linked.Succeeded)
        {
            // Nalog bez veze sa provajderom i bez lozinke je nalog u koji niko
            // ne može da uđe. Bolje ga ukloniti nego ostaviti mrtav red koji
            // zauzima mejl.
            await userManager.DeleteAsync(user);
            logger.LogError("Povezivanje {Provider} naloga nije uspelo: {Errors}",
                id.Provider, string.Join(", ", linked.Errors.Select(e => e.Code)));
            return (null, "Nalog nije mogao biti napravljen. Pokušaj ponovo.");
        }

        // Referral: kod upisan na ekranu ima prednost nad kodom koji je
        // korisnik uneo pre dugmeta za prijavu.
        var kod = string.IsNullOrWhiteSpace(req.ReferralCode) ? referralIzPrijave : req.ReferralCode;
        await authService.SacuvajReferralAsync(user, kod?.Trim().ToUpperInvariant());

        // Kod registracije lozinkom prva rata ide tek kad korisnik klikne
        // verifikacioni link. Ovde je mejl već potvrđen, pa ide odmah.
        await referralService.TryRewardSignupAsync(user.Id);

        // Id, ne mejl — lični podatak ne pripada logu.
        logger.LogInformation("Novi korisnik {UserId} registrovan preko {Provider}.", user.Id, id.Provider);

        return (user, null);
    }

    /// <summary>
    /// Povezuje postojeći nalog sa provajderom.
    ///
    /// ZAŠTITA OD PREUZIMANJA UNAPRED. Napadač može da registruje tuđ mejl
    /// lozinkom i da nikad ne potvrdi nalog. Kad se pravi vlasnik posle
    /// prijavi preko Google-a, nalog bi se povezao i mejl označio potvrđenim —
    /// a napadačeva lozinka bi od tog trenutka RADILA.
    ///
    /// Zato: ako mejl nije bio potvrđen, lozinka se uklanja pre povezivanja.
    /// Pravi vlasnik ništa ne gubi — ulazi preko Google-a, a lozinku može da
    /// postavi kroz „Zaboravljena lozinka".
    /// </summary>
    private async Task PoveziAsync(ApplicationUser user, ExternalIdentity id)
    {
        if (!user.EmailConfirmed)
        {
            if (await userManager.HasPasswordAsync(user))
                await userManager.RemovePasswordAsync(user);

            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);

            // Poništava sve postojeće sesije i linkove vezane za stari potpis.
            await userManager.UpdateSecurityStampAsync(user);

            // Mejl je sada dokazano stvaran — isto mesto u toku kao klik na
            // verifikacioni link.
            await referralService.TryRewardSignupAsync(user.Id);

            logger.LogWarning(
                "Nepotvrđen nalog {UserId} povezan sa {Provider}; lozinka uklonjena.",
                user.Id, id.Provider);
        }

        var result = await userManager.AddLoginAsync(
            user, new UserLoginInfo(id.Provider, id.ProviderKey, id.Provider));

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"AddLoginAsync nije uspeo: {string.Join(", ", result.Errors.Select(e => e.Code))}");

        logger.LogInformation("Nalog {UserId} povezan sa {Provider}.", user.Id, id.Provider);
    }
}
