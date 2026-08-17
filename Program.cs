using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Hubs;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Middleware;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

// ── Serilog ────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Lokalne tajne ──────────────────────────────────────────────────────────
// appsettings.Local.json je gitignore-ovan i drži prave vrednosti za razvojnu
// mašinu. Učitava se POSLE appsettings.{Environment}.json pa ga nadjačava, a
// environment varijable i dalje nadjačavaju njega (produkcijski put).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// AddJsonFile ide na kraj lanca, pa bi inače nadjačao i environment varijable.
// Vraćamo ih na vrh prioriteta: Local.json sme da nadjača appsettings.*.json,
// ali produkcijske env varijable moraju da nadjačaju sve.
builder.Configuration.AddEnvironmentVariables();
if (args.Length > 0)
    builder.Configuration.AddCommandLine(args);

// Padne odmah, sa jasnom porukom, ako neka obavezna tajna nedostaje.
SecretsGuard.Validate(builder.Configuration, builder.Environment);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console());

// ── EF Core + SQL Server ───────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── ASP.NET Identity ────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit           = true;
    options.Password.RequiredLength         = 8;
    options.Password.RequireUppercase       = false;
    options.Password.RequireNonAlphanumeric = false;
    options.User.RequireUniqueEmail         = true;
    // U produkciji nalog mora biti potvrđen. U razvoju ostaje isključeno da
    // testiranje ne zavisi od SMTP-a. (AuthService.LoginAsync ionako proverava
    // EmailConfirmed ručno, ovo je drugi sloj odbrane.)
    options.SignIn.RequireConfirmedEmail    = builder.Environment.IsProduction();

    // Zaključavanje naloga posle uzastopnih promašaja — uz rate limiting po IP
    // ovo pokriva i distribuirani napad na jedan nalog.
    options.Lockout.DefaultLockoutTimeSpan   = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts  = 8;
    options.Lockout.AllowedForNewUsers       = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// ── JWT Authentication + SignalR query-string token ───────────────────────
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtSecret  = jwtSection["Secret"] ?? throw new InvalidOperationException("Jwt:Secret nije konfigurisan.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme    = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer           = true,
        ValidateAudience         = true,
        ValidateLifetime         = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer              = jwtSection["Issuer"],
        ValidAudience            = jwtSection["Audience"],
        IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ClockSkew                = TimeSpan.Zero
    };

    // SignalR WebSocket konekcija ne može da šalje HTTP headere —
    // pa klijent prosleđuje JWT kao query string: ?access_token=...
    // Ovde ga "prebacujemo" u header pre validacije.
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            var path  = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(token) && path.StartsWithSegments("/hubs"))
                context.Token = token;

            return Task.CompletedTask;
        },

        // Access token je stateless i važi do isteka (60 min). Bez ove provere
        // obrisan ili deaktiviran nalog bi nastavio da radi ceo taj period —
        // "obriši nalog" ne bi bio odmah stvaran, a admin deaktivacija ne bi
        // odmah delovala. Zato uz svaki zahtev proveravamo da je nalog i dalje
        // aktivan. To je jedan lookup po primarnom ključu (buffer cache), a u
        // Fazi 4 seli se u Redis.
        OnTokenValidated = async context =>
        {
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                context.Fail("Token nema identifikator korisnika.");
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

            var isActive = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => (bool?)u.IsActive)
                .FirstOrDefaultAsync();

            if (isActive != true)
                context.Fail("Nalog je deaktiviran ili obrisan.");
        }
    };
});

builder.Services.AddAuthorization();

// ── SignalR ────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
})
// SignalR ima SVOJ serijalizator, nezavisan od MVC-a — bez ovoga bi poruke
// poslate preko huba (MessageDto.SenderImageUrl) i dalje nosile relativan put.
.AddJsonProtocol(opts =>
{
    opts.PayloadSerializerOptions.TypeInfoResolver =
        new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                MediaUrlJsonModifier.Create(
                    builder.Configuration["App:BaseUrl"]?.TrimEnd('/') ?? string.Empty)
            }
        };
});

// ── Application Services ──────────────────────────────────────────────────
builder.Services.AddHttpClient("GeoApi", c =>
{
    c.Timeout = TimeSpan.FromSeconds(3);
});

// Scoped servisi
builder.Services.AddScoped<TokenService>();
// Registrovan kroz interfejs da bi testovi mogli da ubace implementaciju
// koja hvata poruke umesto da šalje pravi SMTP.
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<GeoService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<CategoryService>();
builder.Services.AddScoped<ListingService>();
builder.Services.AddScoped<ProviderService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<FavoriteService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TokenWalletService>();
builder.Services.AddScoped<BoostService>();
builder.Services.AddScoped<AdminService>();

// Singleton servisi (žive dok god živi aplikacija)
builder.Services.AddSingleton<MessageEncryption>();
builder.Services.AddSingleton<OnlineTracker>();

// Background servis za čišćenje starih poruka
builder.Services.AddHostedService<MessageCleanupService>();
// Background servis za isticanje boost-ova (svakih sat)
builder.Services.AddHostedService<BoostExpiryService>();

// ── Controllers + Swagger ─────────────────────────────────────────────────
// Slike se u bazi čuvaju relativno; pun URL se sastavlja pri serijalizaciji.
var mediaBaseUrl = builder.Configuration["App:BaseUrl"]?.TrimEnd('/') ?? string.Empty;

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());

        opts.JsonSerializerOptions.TypeInfoResolver =
            new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
            {
                Modifiers = { MediaUrlJsonModifier.Create(mediaBaseUrl) }
            };
    });
// [ApiController] podrazumevano vraća ValidationProblemDetails na neispravan
// model — drugačiji oblik od ostatka API-ja ({success, message}), pa je klijent
// prikazivao poruke tipa "NewPassword: Lozinka mora imati...".
// Poruke u DTO-ovima su već na srpskom, pa prosleđujemo prvu doslovno.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var message = context.ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? "Podaci nisu ispravni.";

        return new BadRequestObjectResult(new { success = false, message });
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title   = "Uslužionica API",
        Version = "v1"
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = ParameterLocation.Header,
        Description  = "Unesi JWT token: Bearer {token}"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id   = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ── CORS — mora dozvoliti credentials za SignalR ───────────────────────────
// MAUI klijent ne šalje Origin (nije browser) pa njemu CORS nije ni bitan;
// ovo štiti od toga da bilo koji sajt u browseru pravi credentialed zahteve
// ka API-ju. U produkciji lista mora biti popunjena (Cors:AllowedOrigins).
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins);
        }
        else if (builder.Environment.IsProduction())
        {
            // Bez konfigurisane liste u produkciji ne dozvoljavamo nijedan origin.
            // Ovo je namerno restriktivno — MAUI klijent i dalje radi.
            policy.WithOrigins("https://localhost");
        }
        else
        {
            // Development: Swagger i lokalni alati.
            policy.SetIsOriginAllowed(_ => true);
        }

        policy.AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();   // Obavezno za SignalR WebSocket handshake
    });
});

// ── Rate limiting ──────────────────────────────────────────────────────────
// Auth endpointi su bili potpuno neograničeni — credential stuffing na /login
// i email-bombing preko /register i /resend-verification.
//
// Limiti su u konfiguraciji, ne zakucani, iz dva razloga:
//  • produkcija ih može podesiti bez ponovnog build-a;
//  • u testovima svi zahtevi dolaze sa iste IP adrese pa dele jednu kvotu —
//    test suite bi sam sebe oborio u 429. Testovi ih dižu, a jedna namenska
//    test klasa ih spušta da proveri da limiter zaista radi.
var authPermitLimit   = builder.Configuration.GetValue("RateLimit:AuthPermitLimit",   5);
var authWindowSeconds = builder.Configuration.GetValue("RateLimit:AuthWindowSeconds", 60);
var emailPermitLimit  = builder.Configuration.GetValue("RateLimit:EmailPermitLimit",  3);
var emailWindowSeconds= builder.Configuration.GetValue("RateLimit:EmailWindowSeconds", 900);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Politika po IP adresi. Partition ključ je IP, pa jedan napadač ne može
    // da potroši kvotu svim korisnicima.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitLimit,
                Window      = TimeSpan.FromSeconds(authWindowSeconds),
                QueueLimit  = 0
            }));

    // Blaža politika za endpointe koji šalju email (skuplji su i za nas).
    options.AddPolicy("email", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = emailPermitLimit,
                Window      = TimeSpan.FromSeconds(emailWindowSeconds),
                QueueLimit  = 0
            }));

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            """{"success":false,"message":"Previše pokušaja. Sačekaj malo pa probaj ponovo."}""",
            ct);
    };
});

// ── Health check ───────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")!);

var app = builder.Build();

// ── Migrate + Seed ─────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db          = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    await db.Database.MigrateAsync();
    await SeedRolesAndAdminAsync(roleManager, userManager, builder.Configuration);
}

// ── Middleware pipeline ────────────────────────────────────────────────────
// Exception handler je PRVI da bi uhvatio i greške iz ostalih middleware-a.
app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("Default");
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

// SignalR hub rute
app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();

// ── Seed helper ───────────────────────────────────────────────────────────
static async Task SeedRolesAndAdminAsync(
    RoleManager<IdentityRole>    roleManager,
    UserManager<ApplicationUser> userManager,
    IConfiguration               config)
{
    foreach (var role in new[] { "Admin", "User" })
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    var adminEmail = config["AdminSeed:Email"]!;
    var adminPass  = config["AdminSeed:Password"]!;

    if (await userManager.FindByEmailAsync(adminEmail) is null)
    {
        var admin = new ApplicationUser
        {
            UserName       = adminEmail,
            Email          = adminEmail,
            FullName       = "Administrator",
            EmailConfirmed = true,
            IsActive       = true
        };
        var result = await userManager.CreateAsync(admin, adminPass);
        if (result.Succeeded)
            await userManager.AddToRoleAsync(admin, "Admin");
    }
}

// ── Vidljivost za testove ──────────────────────────────────────────────────
// Top-level statements generišu `internal class Program`, pa
// WebApplicationFactory<Program> iz test assembly-ja ne bi mogao da ga vidi.
// Ovde ga eksplicitno otvaramo. Prazno telo je namerno — samo menjamo vidljivost.
public partial class Program { }
