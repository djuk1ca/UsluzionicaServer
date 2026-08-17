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
///
/// Izuzetak je `EmailConfirmed` — postavlja se direktno jer bi inače svaki
/// test morao da prođe kroz slanje i potvrdu emaila.
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
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var auth  = scope.ServiceProvider.GetRequiredService<AuthService>();

        // Kroz pravu registraciju da referral logika i heširanje budu stvarni.
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

        var user = await users.FindByEmailAsync(email)
                   ?? throw new InvalidOperationException($"Korisnik '{email}' nije nađen posle registracije.");

        user.EmailConfirmed = true;
        if (tokenBalance != 0m) user.TokenBalance = tokenBalance;
        await users.UpdateAsync(user);

        return user;
    }

    /// <summary>Korisnik BEZ potvrđenog emaila — za testove koji dokazuju da ga pravilo odbija.</summary>
    public async Task<ApplicationUser> CreateUnconfirmedUserAsync(
        string email, string fullName = "Nepotvrđeni Korisnik")
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var auth  = scope.ServiceProvider.GetRequiredService<AuthService>();

        await auth.RegisterAsync(new UsluzionicaServer.DTOs.Auth.RegisterRequest
        {
            FullName = fullName, Email = email, Password = DefaultPassword
        });

        return await users.FindByEmailAsync(email)
               ?? throw new InvalidOperationException($"Korisnik '{email}' nije nađen.");
    }

    // ── Provajder ──────────────────────────────────────────────────────────

    /// <summary>Korisnik sa potvrđenim emailom koji je aktivirao provajder nalog.</summary>
    public async Task<(ApplicationUser User, int ProviderProfileId)> CreateProviderAsync(
        string  email,
        string  profession   = "Vodoinstalater",
        decimal tokenBalance = 0m)
    {
        var user = await CreateConfirmedUserAsync(email, tokenBalance: tokenBalance);

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
}
