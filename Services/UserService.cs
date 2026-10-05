using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Users;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class UserService(
    UserManager<ApplicationUser> userManager,
    AppDbContext                 db,
    IWebHostEnvironment          env,
    IConfiguration               config,
    ImageModerationGate          imageGate,
    ILogger<UserService>         logger)
{
    // ── GET profil ─────────────────────────────────────────────────────────
    public async Task<UserProfileDto?> GetProfileAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        return user is null ? null : MapToDto(user);
    }

    // ── UPDATE profil ──────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateProfileAsync(
        string userId, UpdateUserDto dto)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (false, "Korisnik nije pronađen.");

        // Validacija: grad mora biti iz zvanične liste srpskih opština
        if (dto.LastKnownCity is not null &&
            !SerbianMunicipalities.All.Contains(dto.LastKnownCity))
            return (false, $"'{dto.LastKnownCity}' nije prepoznata opština u Srbiji.");

        user.FullName      = dto.FullName.Trim();
        user.LastKnownCity = dto.LastKnownCity;

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return (false, result.Errors.First().Description);

        return (true, null);
    }

    // ── AVATAR upload ──────────────────────────────────────────────────────
    public async Task<(string? Url, string? Error)> UploadAvatarAsync(
        string userId, IFormFile file)
    {
        // Format i ekstenzija se utvrđuju iz SADRŽAJA fajla. Ime i Content-Type
        // koje je klijent poslao se ne koriste — vidi ImageUploads.
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (null, "Korisnik nije pronađen.");

        return await SacuvajAvatarAsync(user, file);
    }

    /// <summary>
    /// Postavlja avatar iz već preuzetih bajtova — profilna slika sa Google-a
    /// ili Facebook-a posle prve prijave.
    ///
    /// Ide kroz ISTI <see cref="SacuvajAvatarAsync"/> kao otpremanje iz
    /// aplikacije: slika sa provajdera nije pouzdanija od slike koju je
    /// korisnik sam izabrao, pa ne sme da preskoči nijednu proveru.
    /// </summary>
    public async Task<(string? Url, string? Error)> PostaviAvatarAsync(string userId, byte[] bajtovi)
    {
        // Korisnik se učitava OVDE, kroz ovaj kontekst. Primljen spolja, mogao
        // bi biti iz drugog DbContext-a — a upis takvog entiteta puca kad je
        // isti red već praćen u ovom.
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (null, "Korisnik nije pronađen.");

        using var tok = new MemoryStream(bajtovi, writable: false);

        var fajl = new FormFile(tok, 0, bajtovi.Length, "file", "avatar")
        {
            // Bez ovoga FormFile.ContentType baca NullReferenceException.
            // Vrednost se nigde ne koristi za odluku — format se utvrđuje iz
            // sadržaja (ImageUploads).
            Headers     = new HeaderDictionary(),
            ContentType = "application/octet-stream"
        };

        return await SacuvajAvatarAsync(user, fajl);
    }

    /// <summary>Provera formata i sadržaja, upis na disk, putanja u bazu.</summary>
    private async Task<(string? Url, string? Error)> SacuvajAvatarAsync(ApplicationUser user, IFormFile file)
    {
        var userId = user.Id;

        // Format i ekstenzija se utvrđuju iz SADRŽAJA fajla. Ime i Content-Type
        // koje je klijent poslao se ne koriste — vidi ImageUploads.
        var (ext, uploadError) = await ImageUploads.ValidateAsync(file, ImageUploads.MaxAvatarBytes);
        if (ext is null)
            return (null, uploadError);

        // Automatska provera sadržaja. Bez listingId — avatar ne pripada
        // oglasu, pa se sumnjiva slika prijavljuje kao korisnik.
        var (cista, moderationError) = await imageGate.ProveriAsync(file, userId, listingId: null);
        if (!cista)
            return (null, moderationError);

        // Putanja: wwwroot/uploads/avatars/{userId}.{ext}
        var fileName  = $"{userId}{ext}";
        var uploadDir = Path.Combine(env.WebRootPath, "uploads", "avatars");
        Directory.CreateDirectory(uploadDir);

        var filePath = Path.Combine(uploadDir, fileName);
        await using (var stream = File.Create(filePath))
            await file.CopyToAsync(stream);

        // U bazu ide RELATIVNA putanja (prenosiva između domena).
        var relativeUrl = $"/uploads/avatars/{fileName}";
        user.ProfileImageUrl = relativeUrl;
        await userManager.UpdateAsync(user);

        // Ovaj metod vraća URL direktno (ne kroz DTO), pa ga MediaUrlJsonModifier
        // ne dohvata — sastavljamo pun URL ovde da klijent dobije prikazivu vrednost.
        var absoluteUrl = MediaUrls.ToAbsolute(relativeUrl, config["App:BaseUrl"] ?? string.Empty)!;

        logger.LogInformation("Avatar uploadovan za korisnika {UserId}: {Url}", userId, relativeUrl);
        return (absoluteUrl, null);
    }

    // ── BRISANJE NALOGA ────────────────────────────────────────────────────
    /// <summary>
    /// Briše nalog korisnika. Apple App Store (smernica 5.1.1(v)) zahteva da
    /// aplikacija koja dozvoljava kreiranje naloga nudi i brisanje iz same
    /// aplikacije.
    ///
    /// Radi se ANONIMIZACIJA, ne fizičko brisanje reda. Tvrdo brisanje bi
    /// kaskadno odnelo i tuđe podatke — konverzacije sagovornika, recenzije
    /// koje ulaze u prosečnu ocenu drugih provajdera, istoriju rezervacija
    /// druge strane. Time bi brisanje jednog naloga oštetilo naloge koji
    /// nemaju veze sa tim zahtevom.
    ///
    /// Posle poziva korisnik je nepovratno odjavljen, ne može se prijaviti,
    /// a njegovo ime i email nigde više nisu vidljivi.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteAccountAsync(
        string userId, string? password, string? confirmation = null)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (false, "Korisnik nije pronađen.");

        // Potvrda — brisanje je nepovratno.
        //
        // Nalog sa lozinkom potvrđuje LOZINKOM, i potvrdna reč je ne zamenjuje:
        // inače bi ukraden JWT (otključan telefon u tuđim rukama) bio dovoljan
        // da se obriše nalog.
        //
        // Nalog BEZ lozinke (napravljen preko Google-a/Facebook-a) nema čime
        // drugim — ranije je ovde pucao na CheckPasswordAsync i takav korisnik
        // NIJE MOGAO da obriše nalog, što Google Play i Apple izričito traže.
        // Za njega se traži potvrdna reč; pristup je već potvrđen važećim JWT-om.
        if (await userManager.HasPasswordAsync(user))
        {
            if (string.IsNullOrEmpty(password) || !await userManager.CheckPasswordAsync(user, password))
                return (false, "Lozinka nije ispravna.");
        }
        else if (!JePotvrdnaRec(confirmation))
        {
            return (false, $"Upiši {PotvrdnaRec} da potvrdiš brisanje naloga.");
        }

        var anonymousId    = Guid.NewGuid().ToString("N")[..12];
        var anonymousEmail = $"obrisan-{anonymousId}@usluzionica.invalid";

        // 1. Arhiviraj oglase — nestaju iz pretrage i sa profila.
        await db.Listings
            .Where(l => l.ProviderProfile.UserId == userId && l.Status != ListingStatus.Archived)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, ListingStatus.Archived));

        // 2. Poništi sve sesije.
        await db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true));

        // 3. Obriši avatar sa diska (relativna putanja → fizički fajl).
        TryDeleteAvatarFile(user.ProfileImageUrl);

        // 3a. Raskini veze sa Google-om i Facebook-om.
        //
        // Bez ovoga bi veza (provajder + id) i dalje pokazivala na anonimizovan,
        // deaktiviran nalog. Kad se ista osoba kasnije ponovo prijavi Google-om,
        // ExternalLoginService bi pronašao TAJ nalog i odbio je kao deaktiviranu
        // — dakle ne bi mogla ni da napravi nov nalog. Brisanje mora da znači
        // da osoba može da počne ispočetka.
        foreach (var veza in await userManager.GetLoginsAsync(user))
            await userManager.RemoveLoginAsync(user, veza.LoginProvider, veza.ProviderKey);

        // 3b. Uređaji za push. Token pripada telefonu; ostao bi vezan za nalog
        // koji više ne postoji.
        await db.DeviceTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync();

        // 4. Anonimizuj nalog.
        user.FullName        = "Obrisan nalog";
        user.Email           = anonymousEmail;
        user.NormalizedEmail = anonymousEmail.ToUpperInvariant();
        user.UserName        = anonymousEmail;
        user.NormalizedUserName = anonymousEmail.ToUpperInvariant();
        user.ProfileImageUrl = null;
        user.LastKnownCity   = null;
        user.ReferralCode    = null;   // kod se oslobađa, postojeći referrali ostaju
        user.IsActive        = false;
        user.EmailConfirmed  = false;
        user.PhoneNumber     = null;

        // Lozinka se uklanja — nalog se ne može otključati ni pogađanjem.
        user.PasswordHash = null;
        await userManager.UpdateSecurityStampAsync(user);

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return (false, result.Errors.First().Description);

        logger.LogInformation("Nalog obrisan (anonimizovan): {UserId}", userId);
        return (true, null);
    }

    /// <summary>Reč kojom nalog bez lozinke potvrđuje brisanje.</summary>
    public const string PotvrdnaRec = "OBRIŠI";

    /// <summary>
    /// Prihvata i bez dijakritike („OBRISI") i malim slovima — srpska tastatura
    /// nije uvek pri ruci, a cilj je svesna radnja, ne test kucanja.
    /// </summary>
    private static bool JePotvrdnaRec(string? unos) =>
        unos?.Trim().ToUpperInvariant() is "OBRIŠI" or "OBRISI";

    private void TryDeleteAvatarFile(string? relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl)) return;

        try
        {
            var fileName = Path.GetFileName(MediaUrls.ToRelative(relativeUrl));
            if (string.IsNullOrWhiteSpace(fileName)) return;

            var path = Path.Combine(env.WebRootPath, "uploads", "avatars", fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            // Zaostala datoteka nije razlog da brisanje naloga padne.
            logger.LogWarning(ex, "Brisanje avatara nije uspelo: {Url}", relativeUrl);
        }
    }


    // ── Mapper ─────────────────────────────────────────────────────────────
    private static UserProfileDto MapToDto(ApplicationUser user) => new()
    {
        Id              = user.Id,
        FullName        = user.FullName,
        Email           = user.Email!,
        ProfileImageUrl = user.ProfileImageUrl,
        TokenBalance    = user.TokenBalance,
        IsProvider      = user.IsProvider,
        LastKnownCity   = user.LastKnownCity,
        ReferralCode    = user.ReferralCode,
        CreatedAt       = user.CreatedAt,
        HasPassword     = user.PasswordHash is not null
    };
}
