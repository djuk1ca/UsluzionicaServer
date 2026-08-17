using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;          // ConfigureTestServices
using Microsoft.Extensions.Configuration;     // AddInMemoryCollection
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Podiže pravi ASP.NET Core host u memoriji, ali usmeren na SQL Server iz
/// Testcontainers kontejnera.
///
/// Ovo NIJE mock aplikacije — to je ta ista aplikacija: isti Program.cs, isti
/// DI kontejner, isti middleware pipeline, iste migracije. Menjaju se samo
/// spoljne granice (baza i email).
/// </summary>
public sealed class UsluzionicaWebFactory(string connectionString) : WebApplicationFactory<Program>
{
    /// <summary>Instanca koju testovi čitaju da bi videli "poslate" emailove.</summary>
    public FakeEmailService Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // ── Okruženje ───────────────────────────────────────────────────────
        // Namerno NIJE "Production": SecretsGuard tamo primenjuje stroža
        // pravila, a Identity uključuje RequireConfirmedEmail (vezano za
        // IsProduction()), što bi zakomplikovalo pripremu svakog testa.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            // ZAMKA KOJU TREBA RAZUMETI:
            // Program.cs posle učitavanja appsettings.Local.json PONOVO poziva
            // AddEnvironmentVariables(). To znači da env varijable nadjačavaju
            // sve — uključujući i ovo što ovde dodajemo. Zato se prava
            // vrednost connection stringa postavlja kao ENV VARIJABLA u
            // DatabaseFixture, pre nego što se host uopšte napravi.
            //
            // Ovaj blok ostaje kao zaštitni sloj (ako neko pokrene testove bez
            // fixture-a) i kao mesto za podešavanja koja nisu tajne.
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,

                // Vrednosti koje SecretsGuard zahteva. Nijedna nije sa liste
                // kompromitovanih, JWT secret je duži od 32 znaka, a AES ključ
                // dekodira u tačno 32 bajta.
                ["Jwt:Secret"]            = TestSecrets.JwtSecret,
                ["Jwt:Issuer"]            = "usluzionica-api",
                ["Jwt:Audience"]          = "usluzionica-app",
                ["Encryption:MessageKey"] = TestSecrets.AesKeyBase64,
                ["AdminSeed:Email"]       = TestSecrets.AdminEmail,
                ["AdminSeed:Password"]    = TestSecrets.AdminPassword,

                ["App:BaseUrl"]           = "https://test.usluzionica.rs",

                // Rate limiti se dižu vrlo visoko. Razlog: u WebApplicationFactory
                // svi zahtevi dolaze sa iste (prazne) IP adrese, pa ceo test
                // suite deli JEDNU kvotu i sam sebe obori u 429.
                // Klasa koja testira sam limiter ih spušta preko WithRateLimits().
                ["RateLimit:AuthPermitLimit"]   = "100000",
                ["RateLimit:EmailPermitLimit"]  = "100000",

                // Eksplicitno postavljeno jer se appsettings i kod razilaze:
                // kod podrazumeva 3 dana, appsettings.json ima 0 (čime je
                // pravilo isključeno). Testovi ne smeju zavisiti od te zbrke.
                ["Booking:ExecuteAfterDays"]              = "3",
                ["Booking:ServiceRewardTokens"]           = "0.50",
                ["Referral:ProviderActivationRewardTokens"] = "5.0",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // ── Ukloni pozadinske servise ───────────────────────────────────
            // MessageCleanupService odmah po startu radi ExecuteDeleteAsync nad
            // porukama; BoostExpiryService menja oglase i šalje notifikacije.
            // Oba bi nedeterministički kvarila test podatke — test koji kreira
            // poruku sa starim datumom ili boost pred istek bio bi nestabilan.
            services.RemoveAll<IHostedService>();

            // ── Zameni email ────────────────────────────────────────────────
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(Email);
        });
    }
}

/// <summary>Tajne za test host. Držane na jednom mestu da se ne prepisuju po fajlovima.</summary>
internal static class TestSecrets
{
    public const string JwtSecret     = "integration-test-jwt-secret-dovoljno-dug-za-hmac-sha256";
    /// <summary>Base64 od 32 bajta — AES-256 ne prima ništa drugo.</summary>
    public const string AesKeyBase64  = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    public const string AdminEmail    = "admin@test.usluzionica.rs";
    public const string AdminPassword = "AdminTest123!";
}
