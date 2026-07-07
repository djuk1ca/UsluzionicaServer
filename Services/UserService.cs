using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Users;
using UsluzionicaServer.Infrastructure;

namespace UsluzionicaServer.Services;

public sealed class UserService(
    UserManager<ApplicationUser> userManager,
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

        // Javni URL koji klijent koristi za prikaz slike
        var baseUrl = config["App:BaseUrl"] ?? "https://localhost:7176";
        var url     = $"{baseUrl}/uploads/avatars/{fileName}";

        // Snimi URL u bazu
        user.ProfileImageUrl = url;
        await userManager.UpdateAsync(user);

        logger.LogInformation("Avatar uploadovan za korisnika {UserId}: {Url}", userId, url);
        return (url, null);
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
