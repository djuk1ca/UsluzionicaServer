using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Priprema podataka za testove.
///
/// Namerno ide kroz PRAVE servise i UserManager, a ne kroz `db.Add(...)`.
/// Razlog: Identity heširanje lozinke, normalizacija emaila, generisanje
/// referral koda i vezivanje kategorija su deo pravila koja testiramo. Da
/// ubacujemo redove direktno, testirali bismo izmišljeno stanje koje se u
/// produkciji nikad ne pojavljuje.
/// </summary>
public sealed class TestData(UsluzionicaWebFactory factory)
{
    /// <summary>Grad iz zvanične liste opština — validacija ga zahteva.</summary>
    public const string ValidCity = "Subotica";

    /// <summary>Podkategorija iz seed podataka (188 kategorija dolazi iz migracija).</summary>
    public const int SeededCategoryId = 2;

    public const string DefaultPassword = "TestLoz123!";

    // ── Korisnici ──────────────────────────────────────────────────────────

    /// <summary>Korisnik sa potvrđenim emailom — spreman za sve tokove.</summary>
    public async Task<ApplicationUser> CreateConfirmedUserAsync(
        string  email,
        string  fullName     = "Test Korisnik",
        decimal tokenBalance = 0m,
        string? referralCode = null)
    {
        await CreateUnconfirmedUserAsync(email, fullName, referralCode);
        await ConfirmEmailAsync(email);

        if (tokenBalance != 0m)
        {
            var user = await FindAsync(email);
            await SetTokenBalanceAsync(user.Id, tokenBalance);
        }

        return await FindAsync(email);
    }

    /// <summary>Korisnik BEZ potvrđenog emaila — za testove koji dokazuju da ga pravilo odbija.</summary>
    public async Task<ApplicationUser> CreateUnconfirmedUserAsync(
        string  email,
        string  fullName     = "Nepotvrđeni Korisnik",
        string? referralCode = null)
    {
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

        var (ok, errors) = await auth.RegisterAsync(new UsluzionicaServer.DTOs.Auth.RegisterRequest
        {
            FullName     = fullName,
            Email        = email,
            Password     = DefaultPassword,
            ReferralCode = referralCode
        });

        if (!ok)
            throw new InvalidOperationException(
                $"Priprema korisnika '{email}' nije uspela: {string.Join(", ", errors)}");

        return await FindAsync(email);
    }

    /// <summary>
    /// Potvrđuje email PRAVIM Identity tokenom i okida prvu referral ratu —
    /// tačno dva koraka koje radi AuthController.VerifyEmail.
    ///
    /// Zašto ne prosto `user.EmailConfirmed = true`: od izmene referral sistema
    /// potvrda emaila je trenutak isplate prve rate. Da se zastavica postavlja
    /// direktno, nijedan test ne bi mogao da dokaže da se ta rata isplaćuje —
    /// a priprema podataka bi tiho zaobilazila pravilo koje testiramo.
    /// </summary>
    public async Task ConfirmEmailAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var users     = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var referrals = scope.ServiceProvider.GetRequiredService<ReferralService>();

        var user = await users.FindByEmailAsync(email)
                   ?? throw new InvalidOperationException($"Korisnik '{email}' nije nađen.");

        if (user.EmailConfirmed) return;

        var token  = await users.GenerateEmailConfirmationTokenAsync(user);
        var result = await users.ConfirmEmailAsync(user, token);

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Potvrda emaila '{email}' nije uspela: " +
                string.Join(", ", result.Errors.Select(e => e.Description)));

        await referrals.TryRewardSignupAsync(user.Id);
    }

    // ── Provajder ──────────────────────────────────────────────────────────

    /// <summary>Korisnik sa potvrđenim emailom koji je aktivirao provajder nalog.</summary>
    public async Task<(ApplicationUser User, int ProviderProfileId)> CreateProviderAsync(
        string  email,
        string  profession   = "Vodoinstalater",
        decimal tokenBalance = 0m,
        string  fullName     = "Test Korisnik")
    {
        var user = await CreateConfirmedUserAsync(email, fullName: fullName, tokenBalance: tokenBalance);

        using var scope = factory.Services.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<ProviderService>();

        var (profile, error) = await providers.ActivateAsync(user.Id, new ActivateProviderDto
        {
            Profession  = profession,
            Location    = ValidCity,
            CategoryIds = [SeededCategoryId]
        });

        if (profile is null)
            throw new InvalidOperationException($"Aktivacija provajdera '{email}' nije uspela: {error}");

        return (user, profile.ProviderProfileId);
    }

    // ── Oglas ──────────────────────────────────────────────────────────────

    public async Task<int> CreateActiveListingAsync(
        string providerUserId,
        string title      = "Popravka slavine",
        string location   = ValidCity,
        int    categoryId = SeededCategoryId)
    {
        using var scope = factory.Services.CreateScope();
        var listings = scope.ServiceProvider.GetRequiredService<ListingService>();

        var (listing, error) = await listings.CreateAsync(providerUserId, new CreateListingDto
        {
            Title       = title,
            Description = $"Opis za: {title}",
            Location    = location,
            CategoryId  = categoryId,
            PriceMode   = PriceMode.Fixed,
            FixedPrice  = 2000m
        });

        if (listing is null)
            throw new InvalidOperationException($"Kreiranje oglasa '{title}' nije uspelo: {error}");

        return listing.Id;
    }

    // ── Direktne izmene stanja ─────────────────────────────────────────────

    /// <summary>
    /// Postavlja balans direktno. Koristi se kad test treba tačan iznos, a put
    /// kojim su tokeni stigli nije predmet tog testa.
    /// </summary>
    public async Task SetTokenBalanceAsync(string userId, decimal balance)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenBalance, balance));
    }

    /// <summary>Trenutni balans, pročitan kroz nov DbContext (bez keša).</summary>
    public async Task<decimal> GetTokenBalanceAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.TokenBalance)
            .FirstAsync();
    }

    /// <summary>Referral kod korisnika — ono što se deli prijateljima.</summary>
    public async Task<string> GetReferralCodeAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.ReferralCode!)
            .FirstAsync();
    }

    // ── Pomoćno ────────────────────────────────────────────────────────────

    private async Task<ApplicationUser> FindAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return await users.FindByEmailAsync(email)
               ?? throw new InvalidOperationException($"Korisnik '{email}' nije nađen.");
    }
}
