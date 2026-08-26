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
        // Validacija fajla
        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowed.Contains(file.ContentType))
            return (null, "Dozvoljeni formati: JPEG, PNG, WebP.");

        const long maxBytes = 5 * 1024 * 1024; // 5 MB
        if (file.Length > maxBytes)
            return (null, "Slika ne sme biti veća od 5 MB.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (null, "Korisnik nije pronađen.");

        // Putanja: wwwroot/uploads/avatars/{userId}.{ext}
        var ext       = Path.GetExtension(file.FileName).ToLowerInvariant();
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
        string userId, string password)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return (false, "Korisnik nije pronađen.");

        // Potvrda lozinkom — brisanje je nepovratno.
        if (!await userManager.CheckPasswordAsync(user, password))
            return (false, "Lozinka nije ispravna.");

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
        CreatedAt       = user.CreatedAt
    };
}
