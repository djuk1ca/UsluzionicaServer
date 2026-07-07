using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Hubs;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

// ── Serilog ────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

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
    options.SignIn.RequireConfirmedEmail    = false; // true u produkciji
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
        }
    };
});

builder.Services.AddAuthorization();

// ── SignalR ────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// ── Application Services ──────────────────────────────────────────────────
builder.Services.AddHttpClient("GeoApi", c =>
{
    c.Timeout = TimeSpan.FromSeconds(3);
});

// Scoped servisi
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<EmailService>();
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
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
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
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy
            .SetIsOriginAllowed(_ => true) // SignalR zahteva explicit origin, ne wildcard
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());          // Obavezno za SignalR WebSocket handshake
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
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseStaticFiles();
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
