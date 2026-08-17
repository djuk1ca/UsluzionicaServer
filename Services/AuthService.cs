using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Auth;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class AuthService(
    UserManager<ApplicationUser> userManager,
    AppDbContext                 db,
    IEmailService                emailService,
    TokenService                 tokenService,
    GeoService                   geoService,
    IConfiguration               config,
    ILogger<AuthService>         logger)
{
    // ── REGISTER ───────────────────────────────────────────────────────────
    public async Task<(bool Success, string[] Errors)> RegisterAsync(RegisterRequest req)
    {
        // 1. Proveri da email nije zauzet (Identity to radi interno, ali dajemo jasniju grešku)
        if (await userManager.FindByEmailAsync(req.Email) is not null)
            return (false, ["Email je već registrovan."]);

        // 2. Generiši jedinstven referral kod (retry ako kolizija)
        string referralCode;
        do { referralCode = TokenService.GenerateReferralCode(); }
        while (await db.Users.AnyAsync(u => u.ReferralCode == referralCode));

        // 3. Kreiraj korisnika
        var user = new ApplicationUser
        {
            UserName     = req.Email,
            Email        = req.Email,
            FullName     = req.FullName.Trim(),
            ReferralCode = referralCode,
            IsActive     = true
        };
        
        var result = await userManager.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return (false, result.Errors.Select(e => e.Description).ToArray());

        // 4. Dodeli User rolu
        await userManager.AddToRoleAsync(user, "User");

        // 5. Ako je prosleđen referral kod — pronađi referrera i snimi pending zapis
        //    Greška u ovom koraku ne sme blokirati registraciju → catch i log
        if (!string.IsNullOrWhiteSpace(req.ReferralCode))
        {
            try
            {
                var referrer = await db.Users
                    .FirstOrDefaultAsync(u => u.ReferralCode == req.ReferralCode);

                if (referrer is not null && referrer.Id != user.Id)
                {
                    db.Referrals.Add(new Referral
                    {
                        ReferrerId     = referrer.Id,
                        ReferredUserId = user.Id,
                        ReferralCode   = req.ReferralCode
                        // Status = Pending (default)
                    });
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Referral kod '{Code}' nije mogao biti obrađen.", req.ReferralCode);
            }
        }

        // 6. Generiši email verifikacioni token i pošalji email
        //    UserManager.GenerateEmailConfirmationTokenAsync() vraća kriptografski token
        //    koji se čuva interno (AspNetUserTokens tabela) i važi 24 sata (default Identity)
        try
        {
            var token     = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var baseUrl   = config["App:BaseUrl"] ?? "https://localhost:7001";
            // Token može sadržati specijalne znakove → URL encode
            var verifyUrl = $"{baseUrl}/api/auth/verify-email" +
                            $"?userId={Uri.EscapeDataString(user.Id)}" +
                            $"&token={Uri.EscapeDataString(token)}";

            await emailService.SendVerificationEmailAsync(user.Email!, user.FullName, verifyUrl);
        }
        catch (Exception ex)
        {
            // Neuspešan email ne poništava registraciju — korisnik može tražiti resend
            logger.LogError(ex, "Email verifikacija nije poslata korisniku {Email}.", req.Email);
        }

        logger.LogInformation("Novi korisnik registrovan: {Email}", req.Email);

        return (true, []);
    }

    // ── LOGIN ──────────────────────────────────────────────────────────────
    public async Task<(AuthResponse? Response, string? Error)> LoginAsync(
        LoginRequest req, string? ipAddress)
    {
        // 1. Pronađi korisnika
        var user = await userManager.FindByEmailAsync(req.Email);
        if (user is null)
            return (null, "Pogrešan email ili lozinka.");

        // 1a. Nalog privremeno zaključan zbog previše promašaja
        if (await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning("Pokušaj prijave na zaključan nalog: {Email}", req.Email);
            return (null, "Nalog je privremeno zaključan zbog previše neuspelih pokušaja. Pokušaj ponovo za 15 minuta.");
        }

        // 1b. Provera lozinke — kroz AccessFailed/ResetAccessFailed da bi
        //     lockout brojač iz Identity konfiguracije zaista radio.
        //     CheckPasswordAsync sam po sebi NE broji promašaje.
        if (!await userManager.CheckPasswordAsync(user, req.Password))
        {
            await userManager.AccessFailedAsync(user);
            return (null, "Pogrešan email ili lozinka.");
        }

        await userManager.ResetAccessFailedCountAsync(user);

        // 2. Proveri da li je nalog aktivan
        if (!user.IsActive)
            return (null, "Nalog je deaktiviran. Kontaktiraj podršku.");

        // 3. Proveri email verifikaciju
        if (!user.EmailConfirmed)
            return (null, "Email adresa nije potvrđena. Proveri inbox i klikni verifikacioni link.");

        // 4. IP geolokacija → ažuriraj LastKnownCity (ne blokira login ako padne)
        var city = await geoService.GetCityAsync(ipAddress);
        if (city is not null)
            user.LastKnownCity = city;

        // 5. Generiši JWT access token
        var roles       = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.GenerateAccessToken(user, roles);

        // 6. Generiši refresh token i snimi ga u bazu
        //    Stari aktivni refresh tokeni ostaju (podržavamo više uređaja)
        var refreshTokenValue = TokenService.GenerateRefreshToken();
        var refreshExpDays    = int.Parse(config["Jwt:RefreshTokenExpirationDays"] ?? "30");

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId    = user.Id,
            Token     = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddDays(refreshExpDays)
        });

        await db.SaveChangesAsync();

        logger.LogInformation("Korisnik se prijavio: {Email} | IP: {IP} | Grad: {City}",
            req.Email, ipAddress, city ?? "nepoznat");

        return (new AuthResponse
        {
            AccessToken  = accessToken,
            RefreshToken = refreshTokenValue,
            User = new UserDto
            {
                Id            = user.Id,
                FullName      = user.FullName,
                Email         = user.Email!,
                TokenBalance  = user.TokenBalance,
                IsProvider    = user.IsProvider,
                IsAdmin       = roles.Contains("Admin"),
                Roles         = [.. roles],
                LastKnownCity = user.LastKnownCity,
                ReferralCode  = user.ReferralCode
            }
        }, null);
    }

    // ── REFRESH ────────────────────────────────────────────────────────────
    public async Task<(AuthResponse? Response, string? Error)> RefreshAsync(string refreshToken)
    {
        // 1. Pronađi token u bazi zajedno sa korisnikom
        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == refreshToken);

        if (stored is null)
            return (null, "Refresh token nije pronađen.");

        if (stored.IsRevoked)
            return (null, "Refresh token je već poništen. Prijavi se ponovo.");

        if (stored.ExpiresAt < DateTime.UtcNow)
            return (null, "Refresh token je istekao. Prijavi se ponovo.");

        var user = stored.User;

        // 2. Rotacija — stari token revoke-ujemo, pravimo novi
        //    Ovo sprečava da ukradeni token može biti iskorišćen drugi put
        stored.IsRevoked = true;

        var newRefreshValue = TokenService.GenerateRefreshToken();
        var refreshExpDays  = int.Parse(config["Jwt:RefreshTokenExpirationDays"] ?? "30");

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId    = user.Id,
            Token     = newRefreshValue,
            ExpiresAt = DateTime.UtcNow.AddDays(refreshExpDays)
        });

        // 3. Nov access token
        var roles       = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.GenerateAccessToken(user, roles);

        await db.SaveChangesAsync();

        logger.LogInformation("Refresh token rotiran za korisnika: {Email}", user.Email);

        return (new AuthResponse
        {
            AccessToken  = accessToken,
            RefreshToken = newRefreshValue,
            User = new UserDto
            {
                Id            = user.Id,
                FullName      = user.FullName,
                Email         = user.Email!,
                TokenBalance  = user.TokenBalance,
                IsProvider    = user.IsProvider,
                IsAdmin       = roles.Contains("Admin"),
                Roles         = [.. roles],
                LastKnownCity = user.LastKnownCity,
                ReferralCode  = user.ReferralCode
            }
        }, null);
    }

    // ── LOGOUT ─────────────────────────────────────────────────────────────
    public async Task LogoutAsync(string refreshToken)
    {
        var stored = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.Token == refreshToken);

        // Ako token ne postoji ili je već revoked — ne radimo ništa
        // (uvek vraćamo 200 da ne otkrivamo da li token postoji)
        if (stored is null || stored.IsRevoked) return;

        stored.IsRevoked = true;
        await db.SaveChangesAsync();

        logger.LogInformation("Refresh token poništen (logout).");
    }

    // ── RESEND VERIFICATION ────────────────────────────────────────────────
    public async Task ResendVerificationAsync(string email)
    {
        var user = await userManager.FindByEmailAsync(email);

        // Tiho ignorišemo ako korisnik ne postoji ili je već verifikovan
        // (ne otkrivamo da li email postoji u sistemu)
        if (user is null || user.EmailConfirmed) return;

        try
        {
            var token     = await userManager.GenerateEmailConfirmationTokenAsync(user);
            var baseUrl   = config["App:BaseUrl"] ?? "https://localhost:7176";
            var verifyUrl = $"{baseUrl}/api/auth/verify-email" +
                            $"?userId={Uri.EscapeDataString(user.Id)}" +
                            $"&token={Uri.EscapeDataString(token)}";

            await emailService.SendVerificationEmailAsync(user.Email!, user.FullName, verifyUrl);
            logger.LogInformation("Verifikacioni email ponovo poslat na: {Email}", email);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resend verifikacije nije uspeo za: {Email}", email);
        }
    }

    // ── FORGOT PASSWORD ────────────────────────────────────────────────────
    private const int ResetCodeValidMinutes = 15;
    private const int ResetCodeMaxAttempts  = 5;

    /// <summary>
    /// Šalje 6-cifreni kod za reset lozinke.
    /// Uvek "uspeva" iz ugla pozivaoca — ne otkrivamo da li email postoji
    /// (inače bi endpoint postao alat za nabrajanje naloga).
    /// </summary>
    public async Task ForgotPasswordAsync(string email)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null || !user.IsActive)
        {
            logger.LogInformation("Reset lozinke tražen za nepostojeći/neaktivan nalog: {Email}", email);
            return;
        }

        // Poništi sve ranije neiskorišćene kodove — u svakom trenutku važi samo poslednji.
        await db.PasswordResetCodes
            .Where(c => c.UserId == user.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, DateTime.UtcNow));

        var code = GenerateNumericCode();

        db.PasswordResetCodes.Add(new PasswordResetCode
        {
            UserId    = user.Id,
            CodeHash  = HashCode(code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(ResetCodeValidMinutes),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        try
        {
            await emailService.SendPasswordResetEmailAsync(user.Email!, user.FullName, code);
            logger.LogInformation("Kod za reset lozinke poslat na: {Email}", email);
        }
        catch (Exception ex)
        {
            // Kod je već upisan; korisnik može tražiti novi. Ne rušimo zahtev.
            logger.LogError(ex, "Slanje koda za reset lozinke nije uspelo za: {Email}", email);
        }
    }

    // ── RESET PASSWORD ─────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> ResetPasswordAsync(
        string email, string code, string newPassword)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user is null)
            return (false, "Kod nije ispravan ili je istekao.");

        var entry = await db.PasswordResetCodes
            .Where(c => c.UserId == user.Id && c.UsedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (entry is null || entry.ExpiresAt <= DateTime.UtcNow)
            return (false, "Kod nije ispravan ili je istekao.");

        if (entry.Attempts >= ResetCodeMaxAttempts)
        {
            // Potroši kod — dalje pogađanje nema smisla.
            entry.UsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return (false, "Previše pogrešnih pokušaja. Zatraži novi kod.");
        }

        // Poređenje u konstantnom vremenu — heševi su iste dužine.
        var provided = HashCode(code);
        var matches  = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(provided),
            System.Text.Encoding.UTF8.GetBytes(entry.CodeHash));

        if (!matches)
        {
            entry.Attempts++;
            await db.SaveChangesAsync();
            return (false, "Kod nije ispravan ili je istekao.");
        }

        // Kod je tačan → Identity-jev pravi reset token pa promena lozinke.
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var result     = await userManager.ResetPasswordAsync(user, resetToken, newPassword);

        if (!result.Succeeded)
        {
            // Kod NE trošimo — greška je u lozinci (prekratka i sl.), korisnik
            // treba da pokuša ponovo bez traženja novog koda.
            return (false, string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        entry.UsedAt = DateTime.UtcNow;

        // Sve postojeće sesije se gase — ako je nalog bio kompromitovan,
        // napadačev refresh token prestaje da važi.
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true));

        // Reset lozinke potvrđuje da korisnik ima pristup email adresi.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        await db.SaveChangesAsync();

        logger.LogInformation("Lozinka resetovana za: {Email}", email);
        return (true, null);
    }

    // ── Helperi za kod ─────────────────────────────────────────────────────
    private static string GenerateNumericCode() =>
        System.Security.Cryptography.RandomNumberGenerator
            .GetInt32(0, 1_000_000)
            .ToString("D6");

    private static string HashCode(string code) =>
        Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(code)));
}
