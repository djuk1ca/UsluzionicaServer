using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<RefreshToken>        RefreshTokens        => Set<RefreshToken>();
    public DbSet<PasswordResetCode>   PasswordResetCodes   => Set<PasswordResetCode>();
    public DbSet<Referral>            Referrals            => Set<Referral>();
    public DbSet<Category>            Categories           => Set<Category>();
    public DbSet<ProviderProfile>     ProviderProfiles     => Set<ProviderProfile>();
    public DbSet<ProviderCategory>    ProviderCategories   => Set<ProviderCategory>();
    public DbSet<Listing>             Listings             => Set<Listing>();
    public DbSet<ListingImage>        ListingImages        => Set<ListingImage>();
    public DbSet<Conversation>        Conversations        => Set<Conversation>();
    public DbSet<Message>             Messages             => Set<Message>();
    public DbSet<BookingRequest>      BookingRequests      => Set<BookingRequest>();
    public DbSet<ServiceExecution>    ServiceExecutions    => Set<ServiceExecution>();
    public DbSet<Review>              Reviews              => Set<Review>();
    public DbSet<TokenTransaction>    TokenTransactions    => Set<TokenTransaction>();
    public DbSet<TokenPurchase>       TokenPurchases       => Set<TokenPurchase>();
    public DbSet<ListingBoost>        ListingBoosts        => Set<ListingBoost>();
    public DbSet<DiscountTokenOffer>  DiscountTokenOffers  => Set<DiscountTokenOffer>();
    public DbSet<Notification>        Notifications        => Set<Notification>();
    public DbSet<FavoriteListing>     FavoriteListings     => Set<FavoriteListing>();
    public DbSet<FavoriteProvider>    FavoriteProviders    => Set<FavoriteProvider>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ── ApplicationUser ────────────────────────────────────────────────
        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            e.Property(u => u.TokenBalance).HasColumnType("decimal(18,2)").HasDefaultValue(0m);
            e.Property(u => u.ProfileImageUrl).HasMaxLength(500);
            e.Property(u => u.LastKnownCity).HasMaxLength(100);
            e.Property(u => u.ReferralCode).HasMaxLength(20);
            e.HasIndex(u => u.ReferralCode).IsUnique();
            e.Property(u => u.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

            e.HasMany(u => u.ConversationsAsUser1)
             .WithOne(c => c.User1)
             .HasForeignKey(c => c.User1Id)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(u => u.ConversationsAsUser2)
             .WithOne(c => c.User2)
             .HasForeignKey(c => c.User2Id)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(u => u.BookingsAsClient)
             .WithOne(b => b.Client)
             .HasForeignKey(b => b.ClientId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(u => u.BookingsAsProvider)
             .WithOne(b => b.Provider)
             .HasForeignKey(b => b.ProviderUserId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(u => u.SentMessages)
             .WithOne(m => m.Sender)
             .HasForeignKey(m => m.SenderId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(u => u.ReferralsSent)
             .WithOne(r => r.Referrer)
             .HasForeignKey(r => r.ReferrerId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Referral ───────────────────────────────────────────────────────
        builder.Entity<Referral>(e =>
        {
            e.Property(r => r.ReferralCode).HasMaxLength(20).IsRequired();
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.TokensAwarded).HasColumnType("decimal(18,2)");
            e.HasIndex(r => r.ReferredUserId).IsUnique();  // jedan user = jedna referral veza

            // Referrer → mnogo Referrals (konfigurisano gore na ApplicationUser)

            // ReferredUser → tačno jedan Referral zapis
            e.HasOne(r => r.ReferredUser)
             .WithMany()   // ReferredUser nema kolekciju "ReceivedReferrals"
             .HasForeignKey(r => r.ReferredUserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── RefreshToken ───────────────────────────────────────────────────
        builder.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.Token).HasMaxLength(500).IsRequired();
            e.HasIndex(t => t.Token);
            e.HasOne(t => t.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(t => t.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── PasswordResetCode ──────────────────────────────────────────────
        builder.Entity<PasswordResetCode>(e =>
        {
            e.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();
            // Traženje aktivnog koda za korisnika ide po (UserId, UsedAt).
            e.HasIndex(c => new { c.UserId, c.UsedAt });
            e.HasOne(c => c.User)
             .WithMany()
             .HasForeignKey(c => c.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Category ───────────────────────────────────────────────────────
        builder.Entity<Category>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.Property(c => c.Slug).HasMaxLength(100).IsRequired();
            e.HasIndex(c => c.Slug).IsUnique();
            e.HasOne(c => c.Parent)
             .WithMany(c => c.Children)
             .HasForeignKey(c => c.ParentId)
             .OnDelete(DeleteBehavior.Restrict);

            // Seed — 13 parent kategorija + 175 podkategorija = 188 ukupno
            e.HasData(
                // ── Parent kategorije (ParentId = null) ───────────────────────
                new Category { Id = 1,  Name = "Beauty",                  Slug = "beauty",         SortOrder = 1  },
                new Category { Id = 2,  Name = "Handmade",                Slug = "handmade",       SortOrder = 2  },
                new Category { Id = 3,  Name = "Foto & Video",            Slug = "foto-video",     SortOrder = 3  },
                new Category { Id = 4,  Name = "Majstori",                Slug = "majstori",       SortOrder = 4  },
                new Category { Id = 5,  Name = "Dekoracija & Eventi",     Slug = "dekoracija",     SortOrder = 5  },
                new Category { Id = 6,  Name = "Digital & Marketing",     Slug = "digital",        SortOrder = 6  },
                new Category { Id = 7,  Name = "Auto",                    Slug = "auto",           SortOrder = 7  },
                new Category { Id = 8,  Name = "Usluge",                  Slug = "usluge",         SortOrder = 8  },
                new Category { Id = 9,  Name = "Zdravlje & Wellness",     Slug = "zdravlje",       SortOrder = 9  },
                new Category { Id = 10, Name = "Obrazovanje & Instrukcije", Slug = "obrazovanje",  SortOrder = 10 },
                new Category { Id = 11, Name = "Kućni ljubimci",          Slug = "kucni-ljubimci", SortOrder = 11 },
                new Category { Id = 12, Name = "Sport & Rekreacija",      Slug = "sport",          SortOrder = 12 },
                new Category { Id = 13, Name = "Hrana & Piće",            Slug = "hrana",          SortOrder = 13 },

                // ── Beauty podkategorije (ParentId=1, Ids 14–44) ──────────────
                new Category { Id = 14, Name = "Frizerstvo — žene",          Slug = "frizerstvo-zene",      ParentId = 1, SortOrder = 1  },
                new Category { Id = 15, Name = "Frizerstvo — muškarci",      Slug = "frizerstvo-muskarci",  ParentId = 1, SortOrder = 2  },
                new Category { Id = 16, Name = "Frizerstvo — deca",          Slug = "frizerstvo-deca",      ParentId = 1, SortOrder = 3  },
                new Category { Id = 17, Name = "Boja kose",                  Slug = "boja-kose",            ParentId = 1, SortOrder = 4  },
                new Category { Id = 18, Name = "Keratinski tretmani",        Slug = "keratinski-tretmani",  ParentId = 1, SortOrder = 5  },
                new Category { Id = 19, Name = "Nadogradnja kose",           Slug = "nadogradnja-kose",     ParentId = 1, SortOrder = 6  },
                new Category { Id = 20, Name = "Gel nokti",                  Slug = "gel-nokti",            ParentId = 1, SortOrder = 7  },
                new Category { Id = 21, Name = "Akrilni nokti",              Slug = "akrilni-nokti",        ParentId = 1, SortOrder = 8  },
                new Category { Id = 22, Name = "Pedikir",                    Slug = "pedikir",              ParentId = 1, SortOrder = 9  },
                new Category { Id = 23, Name = "Manikir",                    Slug = "manikir",              ParentId = 1, SortOrder = 10 },
                new Category { Id = 24, Name = "Laminacija obrva",           Slug = "laminacija-obrva",     ParentId = 1, SortOrder = 11 },
                new Category { Id = 25, Name = "Microblading",               Slug = "microblading",         ParentId = 1, SortOrder = 12 },
                new Category { Id = 26, Name = "Nadogradnja trepavica",      Slug = "nadogradnja-trepavica",ParentId = 1, SortOrder = 13 },
                new Category { Id = 27, Name = "Lifting trepavica",          Slug = "lifting-trepavica",    ParentId = 1, SortOrder = 14 },
                new Category { Id = 28, Name = "Svečana šminka",             Slug = "svecana-sminka",       ParentId = 1, SortOrder = 15 },
                new Category { Id = 29, Name = "Svakodnevna šminka",         Slug = "svakodnevna-sminka",   ParentId = 1, SortOrder = 16 },
                new Category { Id = 30, Name = "Trajni makeup — usne",       Slug = "trajni-makeup-usne",   ParentId = 1, SortOrder = 17 },
                new Category { Id = 31, Name = "Trajni makeup — obrve",      Slug = "trajni-makeup-obrve",  ParentId = 1, SortOrder = 18 },
                new Category { Id = 32, Name = "Depilacija vosak",           Slug = "depilacija-vosak",     ParentId = 1, SortOrder = 19 },
                new Category { Id = 33, Name = "Depilacija šećerom",         Slug = "depilacija-secerom",   ParentId = 1, SortOrder = 20 },
                new Category { Id = 34, Name = "Laser depilacija",           Slug = "laser-depilacija",     ParentId = 1, SortOrder = 21 },
                new Category { Id = 35, Name = "Klasična masaža",            Slug = "klasicna-masaza",      ParentId = 1, SortOrder = 22 },
                new Category { Id = 36, Name = "Relaks masaža",              Slug = "relaks-masaza",        ParentId = 1, SortOrder = 23 },
                new Category { Id = 37, Name = "Sportska masaža",            Slug = "sportska-masaza",      ParentId = 1, SortOrder = 24 },
                new Category { Id = 38, Name = "Maderoterapija",             Slug = "maderoterapija",       ParentId = 1, SortOrder = 25 },
                new Category { Id = 39, Name = "Anticelulit masaža",         Slug = "anticelulit-masaza",   ParentId = 1, SortOrder = 26 },
                new Category { Id = 40, Name = "Facial tretman",             Slug = "facial-tretman",       ParentId = 1, SortOrder = 27 },
                new Category { Id = 41, Name = "Hemijski piling",            Slug = "hemijski-piling",      ParentId = 1, SortOrder = 28 },
                new Category { Id = 42, Name = "Mikrodermabrazija",          Slug = "mikrodermabrazija",    ParentId = 1, SortOrder = 29 },
                new Category { Id = 43, Name = "Tattoo",                     Slug = "tattoo",               ParentId = 1, SortOrder = 30 },
                new Category { Id = 44, Name = "Piercing",                   Slug = "piercing",             ParentId = 1, SortOrder = 31 },

                // ── Handmade podkategorije (ParentId=2, Ids 45–58) ───────────
                new Category { Id = 45, Name = "Ručno rađen nakit",          Slug = "rucni-nakit",          ParentId = 2, SortOrder = 1  },
                new Category { Id = 46, Name = "Keramika",                   Slug = "keramika",             ParentId = 2, SortOrder = 2  },
                new Category { Id = 47, Name = "Pletenje i heklanje",        Slug = "pletenje-heklanje",    ParentId = 2, SortOrder = 3  },
                new Category { Id = 48, Name = "Umetničke slike",            Slug = "umetnicke-slike",      ParentId = 2, SortOrder = 4  },
                new Category { Id = 49, Name = "Akvarelne slike",            Slug = "akvarelne-slike",      ParentId = 2, SortOrder = 5  },
                new Category { Id = 50, Name = "Ilustracije i crtež",        Slug = "ilustracije",          ParentId = 2, SortOrder = 6  },
                new Category { Id = 51, Name = "Personalizovani pokloni",    Slug = "personalizovani-pokloni", ParentId = 2, SortOrder = 7 },
                new Category { Id = 52, Name = "Krojenje i šivenje",         Slug = "krojenje-sivenje",     ParentId = 2, SortOrder = 8  },
                new Category { Id = 53, Name = "Šivenje po meri",            Slug = "sivenje-po-meri",      ParentId = 2, SortOrder = 9  },
                new Category { Id = 54, Name = "Sveće",                      Slug = "svece",                ParentId = 2, SortOrder = 10 },
                new Category { Id = 55, Name = "Sapuni",                     Slug = "sapuni",               ParentId = 2, SortOrder = 11 },
                new Category { Id = 56, Name = "Makrame",                    Slug = "makrame",              ParentId = 2, SortOrder = 12 },
                new Category { Id = 57, Name = "Dekorativni predmeti",       Slug = "dekorativni-predmeti", ParentId = 2, SortOrder = 13 },
                new Category { Id = 58, Name = "Vez i tekstil",              Slug = "vez-tekstil",          ParentId = 2, SortOrder = 14 },

                // ── Foto & Video podkategorije (ParentId=3, Ids 59–70) ────────
                new Category { Id = 59, Name = "Foto venčanja",              Slug = "foto-vencanja",        ParentId = 3, SortOrder = 1  },
                new Category { Id = 60, Name = "Foto krštenja i proslava",   Slug = "foto-krstenja",        ParentId = 3, SortOrder = 2  },
                new Category { Id = 61, Name = "Foto portret",               Slug = "foto-portret",         ParentId = 3, SortOrder = 3  },
                new Category { Id = 62, Name = "Foto novorođenčadi",         Slug = "foto-novorodjencadi",  ParentId = 3, SortOrder = 4  },
                new Category { Id = 63, Name = "Foto produkt",               Slug = "foto-produkt",         ParentId = 3, SortOrder = 5  },
                new Category { Id = 64, Name = "Foto nekretnina",            Slug = "foto-nekretnina",      ParentId = 3, SortOrder = 6  },
                new Category { Id = 65, Name = "Foto eventi",                Slug = "foto-eventi",          ParentId = 3, SortOrder = 7  },
                new Category { Id = 66, Name = "Video produkcija",           Slug = "video-produkcija",     ParentId = 3, SortOrder = 8  },
                new Category { Id = 67, Name = "Montaža videa",              Slug = "montaza-videa",        ParentId = 3, SortOrder = 9  },
                new Category { Id = 68, Name = "Drone snimanje",             Slug = "drone-snimanje",       ParentId = 3, SortOrder = 10 },
                new Category { Id = 69, Name = "Reels i kratki video",       Slug = "reels-kratki-video",   ParentId = 3, SortOrder = 11 },
                new Category { Id = 70, Name = "Modna fotografija",          Slug = "modna-fotografija",    ParentId = 3, SortOrder = 12 },

                // ── Majstori podkategorije (ParentId=4, Ids 71–90) ───────────
                new Category { Id = 71, Name = "Vodoinstalater",             Slug = "vodoinstalater",       ParentId = 4, SortOrder = 1  },
                new Category { Id = 72, Name = "Električar",                 Slug = "elektricar",           ParentId = 4, SortOrder = 2  },
                new Category { Id = 73, Name = "Moler",                      Slug = "moler",                ParentId = 4, SortOrder = 3  },
                new Category { Id = 74, Name = "Gletovanje",                 Slug = "gletovanje",           ParentId = 4, SortOrder = 4  },
                new Category { Id = 75, Name = "Stolar",                     Slug = "stolar",               ParentId = 4, SortOrder = 5  },
                new Category { Id = 76, Name = "Bravar",                     Slug = "bravar",               ParentId = 4, SortOrder = 6  },
                new Category { Id = 77, Name = "Keramičar",                  Slug = "keramicar",            ParentId = 4, SortOrder = 7  },
                new Category { Id = 78, Name = "Fasader",                    Slug = "fasader",              ParentId = 4, SortOrder = 8  },
                new Category { Id = 79, Name = "Zidar",                      Slug = "zidar",                ParentId = 4, SortOrder = 9  },
                new Category { Id = 80, Name = "Servis klima uređaja",       Slug = "servis-klima",         ParentId = 4, SortOrder = 10 },
                new Category { Id = 81, Name = "Montaža klima uređaja",      Slug = "montaza-klima",        ParentId = 4, SortOrder = 11 },
                new Category { Id = 82, Name = "Parketar",                   Slug = "parketar",             ParentId = 4, SortOrder = 12 },
                new Category { Id = 83, Name = "Krovopokrivač",              Slug = "krovopokrivac",        ParentId = 4, SortOrder = 13 },
                new Category { Id = 84, Name = "Izolacija",                  Slug = "izolacija",            ParentId = 4, SortOrder = 14 },
                new Category { Id = 85, Name = "Renoviranje kupatila",       Slug = "renoviranje-kupatila", ParentId = 4, SortOrder = 15 },
                new Category { Id = 86, Name = "Renoviranje kuhinje",        Slug = "renoviranje-kuhinje",  ParentId = 4, SortOrder = 16 },
                new Category { Id = 87, Name = "Montaža nameštaja",          Slug = "montaza-namestaja",    ParentId = 4, SortOrder = 17 },
                new Category { Id = 88, Name = "Popravka kućnih aparata",    Slug = "popravka-aparata",     ParentId = 4, SortOrder = 18 },
                new Category { Id = 89, Name = "Čišćenje dimnjaka",          Slug = "ciscenje-dimnjaka",    ParentId = 4, SortOrder = 19 },
                new Category { Id = 90, Name = "Deratizacija i dezinsekcija",Slug = "deratizacija",         ParentId = 4, SortOrder = 20 },

                // ── Dekoracija & Eventi (ParentId=5, Ids 91–104) ─────────────
                new Category { Id = 91,  Name = "Uređenje venčanja",              Slug = "uredjenje-vencanja",    ParentId = 5, SortOrder = 1  },
                new Category { Id = 92,  Name = "Uređenje krštenja",              Slug = "uredjenje-krstenja",    ParentId = 5, SortOrder = 2  },
                new Category { Id = 93,  Name = "Uređenje proslava",              Slug = "uredjenje-proslava",    ParentId = 5, SortOrder = 3  },
                new Category { Id = 94,  Name = "Cvetni aranžmani",              Slug = "cvetni-aranzmani",      ParentId = 5, SortOrder = 4  },
                new Category { Id = 95,  Name = "Balonske dekoracije",            Slug = "balonske-dekoracije",   ParentId = 5, SortOrder = 5  },
                new Category { Id = 96,  Name = "Svetlosne instalacije",          Slug = "svetlosne-instalacije", ParentId = 5, SortOrder = 6  },
                new Category { Id = 97,  Name = "DJ usluge",                      Slug = "dj-usluge",             ParentId = 5, SortOrder = 7  },
                new Category { Id = 98,  Name = "Live muzika — bend",             Slug = "live-muzika",           ParentId = 5, SortOrder = 8  },
                new Category { Id = 99,  Name = "Pevač / Pevačica",              Slug = "pevac-pevacica",        ParentId = 5, SortOrder = 9  },
                new Category { Id = 100, Name = "Fotoboks",                       Slug = "fotoboks",              ParentId = 5, SortOrder = 10 },
                new Category { Id = 101, Name = "Animatori za decu",              Slug = "animatori-decu",        ParentId = 5, SortOrder = 11 },
                new Category { Id = 102, Name = "Iznajmljivanje stolova i stolica",Slug = "iznajmljivanje-stolova",ParentId = 5, SortOrder = 12 },
                new Category { Id = 103, Name = "Catering",                       Slug = "catering",              ParentId = 5, SortOrder = 13 },
                new Category { Id = 104, Name = "Torte i kolači po porudžbini",   Slug = "torte-kolaci",          ParentId = 5, SortOrder = 14 },

                // ── Digital & Marketing (ParentId=6, Ids 105–119) ────────────
                new Category { Id = 105, Name = "Web dizajn",                     Slug = "web-dizajn",            ParentId = 6, SortOrder = 1  },
                new Category { Id = 106, Name = "Web razvoj",                     Slug = "web-razvoj",            ParentId = 6, SortOrder = 2  },
                new Category { Id = 107, Name = "Mobilne aplikacije",             Slug = "mobilne-aplikacije",    ParentId = 6, SortOrder = 3  },
                new Category { Id = 108, Name = "SEO optimizacija",               Slug = "seo",                   ParentId = 6, SortOrder = 4  },
                new Category { Id = 109, Name = "Social media menadžment",        Slug = "social-media",          ParentId = 6, SortOrder = 5  },
                new Category { Id = 110, Name = "Grafički dizajn",                Slug = "graficki-dizajn",       ParentId = 6, SortOrder = 6  },
                new Category { Id = 111, Name = "Logo dizajn",                    Slug = "logo-dizajn",           ParentId = 6, SortOrder = 7  },
                new Category { Id = 112, Name = "Copywriting i tekstovi",         Slug = "copywriting",           ParentId = 6, SortOrder = 8  },
                new Category { Id = 113, Name = "Email marketing",                Slug = "email-marketing",       ParentId = 6, SortOrder = 9  },
                new Category { Id = 114, Name = "Google Ads",                     Slug = "google-ads",            ParentId = 6, SortOrder = 10 },
                new Category { Id = 115, Name = "Facebook & Instagram Ads",       Slug = "fb-ig-ads",             ParentId = 6, SortOrder = 11 },
                new Category { Id = 116, Name = "Video animacije",                Slug = "video-animacije",       ParentId = 6, SortOrder = 12 },
                new Category { Id = 117, Name = "UI/UX dizajn",                   Slug = "ui-ux-dizajn",          ParentId = 6, SortOrder = 13 },
                new Category { Id = 118, Name = "Brend identitet",                Slug = "brend-identitet",       ParentId = 6, SortOrder = 14 },
                new Category { Id = 119, Name = "Fotografija za društvene mreže", Slug = "foto-drustvene-mreze",  ParentId = 6, SortOrder = 15 },

                // ── Auto (ParentId=7, Ids 120–131) ───────────────────────────
                new Category { Id = 120, Name = "Auto mehaničar",                 Slug = "auto-mehanicar",        ParentId = 7, SortOrder = 1  },
                new Category { Id = 121, Name = "Auto dijagnostika",              Slug = "auto-dijagnostika",     ParentId = 7, SortOrder = 2  },
                new Category { Id = 122, Name = "Autopraonica",                   Slug = "autopraonica",          ParentId = 7, SortOrder = 3  },
                new Category { Id = 123, Name = "Auto detailing",                 Slug = "auto-detailing",        ParentId = 7, SortOrder = 4  },
                new Category { Id = 124, Name = "Auto limar",                     Slug = "auto-limar",            ParentId = 7, SortOrder = 5  },
                new Category { Id = 125, Name = "Poliranje i zaštita laka",       Slug = "poliranje-lak",         ParentId = 7, SortOrder = 6  },
                new Category { Id = 126, Name = "Tapaciranje",                    Slug = "tapaciranje",           ParentId = 7, SortOrder = 7  },
                new Category { Id = 127, Name = "Auto električar",                Slug = "auto-elektricar",       ParentId = 7, SortOrder = 8  },
                new Category { Id = 128, Name = "Gume i felne",                   Slug = "gume-felne",            ParentId = 7, SortOrder = 9  },
                new Category { Id = 129, Name = "Vuča vozila",                    Slug = "vuca-vozila",           ParentId = 7, SortOrder = 10 },
                new Category { Id = 130, Name = "Prevoz i transfer",              Slug = "prevoz-transfer",       ParentId = 7, SortOrder = 11 },
                new Category { Id = 131, Name = "Rent-a-car",                     Slug = "rent-a-car",            ParentId = 7, SortOrder = 12 },

                // ── Usluge (ParentId=8, Ids 132–143) ─────────────────────────
                new Category { Id = 132, Name = "Prevod tekstova",                Slug = "prevod-tekstova",       ParentId = 8, SortOrder = 1  },
                new Category { Id = 133, Name = "Simultano prevođenje",           Slug = "simultano-prevodjenje", ParentId = 8, SortOrder = 2  },
                new Category { Id = 134, Name = "Čišćenje stanova",               Slug = "ciscenje-stanova",      ParentId = 8, SortOrder = 3  },
                new Category { Id = 135, Name = "Čišćenje poslovnih prostora",    Slug = "ciscenje-poslovnih",    ParentId = 8, SortOrder = 4  },
                new Category { Id = 136, Name = "Čišćenje tepiha i nameštaja",    Slug = "ciscenje-tepisa",       ParentId = 8, SortOrder = 5  },
                new Category { Id = 137, Name = "Selidbe",                        Slug = "selidbe",               ParentId = 8, SortOrder = 6  },
                new Category { Id = 138, Name = "Briga o deci (dadilja)",         Slug = "dadilja",               ParentId = 8, SortOrder = 7  },
                new Category { Id = 139, Name = "Vrtlarstvo i uređenje dvorišta", Slug = "vrtlarstvo",            ParentId = 8, SortOrder = 8  },
                new Category { Id = 140, Name = "IT podrška i servis računara",   Slug = "it-podrska",            ParentId = 8, SortOrder = 9  },
                new Category { Id = 141, Name = "Računovodstvo i knjigovodstvo",   Slug = "racunovodstvo",         ParentId = 8, SortOrder = 10 },
                new Category { Id = 142, Name = "Pravne usluge i konsultacije",   Slug = "pravne-usluge",         ParentId = 8, SortOrder = 11 },
                new Category { Id = 143, Name = "Organizacija putovanja",         Slug = "organizacija-putovanja",ParentId = 8, SortOrder = 12 },

                // ── Zdravlje & Wellness (ParentId=9, Ids 144–154) ─────────────
                new Category { Id = 144, Name = "Fizioterapeut",                  Slug = "fizioterapeut",         ParentId = 9, SortOrder = 1  },
                new Category { Id = 145, Name = "Radna terapija",                 Slug = "radna-terapija",        ParentId = 9, SortOrder = 2  },
                new Category { Id = 146, Name = "Psiholog i psihoterapeut",       Slug = "psiholog",              ParentId = 9, SortOrder = 3  },
                new Category { Id = 147, Name = "Logoped",                        Slug = "logoped",               ParentId = 9, SortOrder = 4  },
                new Category { Id = 148, Name = "Nutricionista i dijetetičar",    Slug = "nutricionista",         ParentId = 9, SortOrder = 5  },
                new Category { Id = 149, Name = "Akupunktura",                    Slug = "akupunktura",           ParentId = 9, SortOrder = 6  },
                new Category { Id = 150, Name = "Refleksologija",                 Slug = "refleksologija",        ParentId = 9, SortOrder = 7  },
                new Category { Id = 151, Name = "Reiki",                          Slug = "reiki",                 ParentId = 9, SortOrder = 8  },
                new Category { Id = 152, Name = "Meditacija i mindfulness",       Slug = "meditacija",            ParentId = 9, SortOrder = 9  },
                new Category { Id = 153, Name = "Lični coach",                    Slug = "licni-coach",           ParentId = 9, SortOrder = 10 },
                new Category { Id = 154, Name = "Hiropraktičar",                  Slug = "hiroprakticar",         ParentId = 9, SortOrder = 11 },

                // ── Obrazovanje & Instrukcije (ParentId=10, Ids 155–164) ──────
                new Category { Id = 155, Name = "Instrukcije matematika",         Slug = "instrukcije-matematika",ParentId = 10, SortOrder = 1  },
                new Category { Id = 156, Name = "Instrukcije fizika i hemija",    Slug = "instrukcije-fizika",    ParentId = 10, SortOrder = 2  },
                new Category { Id = 157, Name = "Instrukcije srpski jezik",       Slug = "instrukcije-srpski",    ParentId = 10, SortOrder = 3  },
                new Category { Id = 158, Name = "Instrukcije engleski jezik",     Slug = "instrukcije-engleski",  ParentId = 10, SortOrder = 4  },
                new Category { Id = 159, Name = "Instrukcije nemački / francuski / španski", Slug = "instrukcije-strani-jezici", ParentId = 10, SortOrder = 5 },
                new Category { Id = 160, Name = "Muzičke lekcije",                Slug = "muzicke-lekcije",       ParentId = 10, SortOrder = 6  },
                new Category { Id = 161, Name = "Plesne lekcije",                 Slug = "plesne-lekcije",        ParentId = 10, SortOrder = 7  },
                new Category { Id = 162, Name = "Instrukcije programiranja",      Slug = "instrukcije-programiranja", ParentId = 10, SortOrder = 8 },
                new Category { Id = 163, Name = "Instrukcije umetnosti i crtanja",Slug = "instrukcije-umetnosti", ParentId = 10, SortOrder = 9  },
                new Category { Id = 164, Name = "Priprema za prijemni / maturu",  Slug = "priprema-prijemni",     ParentId = 10, SortOrder = 10 },

                // ── Kućni ljubimci (ParentId=11, Ids 165–171) ────────────────
                new Category { Id = 165, Name = "Šišanje kućnih ljubimaca (grooming)", Slug = "grooming",         ParentId = 11, SortOrder = 1 },
                new Category { Id = 166, Name = "Dresura i obuka psa",           Slug = "dresura-psa",           ParentId = 11, SortOrder = 2 },
                new Category { Id = 167, Name = "Petsitting (čuvanje ljubimaca)", Slug = "petsitting",            ParentId = 11, SortOrder = 3 },
                new Category { Id = 168, Name = "Šetanje pasa",                   Slug = "setanje-pasa",          ParentId = 11, SortOrder = 4 },
                new Category { Id = 169, Name = "Veterinarska kućna poseta",      Slug = "vet-kucna-poseta",      ParentId = 11, SortOrder = 5 },
                new Category { Id = 170, Name = "Fotografija kućnih ljubimaca",   Slug = "foto-ljubimci",         ParentId = 11, SortOrder = 6 },
                new Category { Id = 171, Name = "Izrada opreme za ljubimce",      Slug = "oprema-ljubimci",       ParentId = 11, SortOrder = 7 },

                // ── Sport & Rekreacija (ParentId=12, Ids 172–180) ────────────
                new Category { Id = 172, Name = "Lični fitnes trener",            Slug = "fitnes-trener",         ParentId = 12, SortOrder = 1 },
                new Category { Id = 173, Name = "Joga instruktor",                Slug = "joga",                  ParentId = 12, SortOrder = 2 },
                new Category { Id = 174, Name = "Pilates instruktor",             Slug = "pilates",               ParentId = 12, SortOrder = 3 },
                new Category { Id = 175, Name = "Plivanje — instruktor",          Slug = "plivanje-instruktor",   ParentId = 12, SortOrder = 4 },
                new Category { Id = 176, Name = "Tenis — instruktor",             Slug = "tenis-instruktor",      ParentId = 12, SortOrder = 5 },
                new Category { Id = 177, Name = "Fudbal — trener (deca)",         Slug = "fudbal-trener",         ParentId = 12, SortOrder = 6 },
                new Category { Id = 178, Name = "Boks i borilačke veštine",       Slug = "boks-borilacke",        ParentId = 12, SortOrder = 7 },
                new Category { Id = 179, Name = "Planinarenje — vodič",           Slug = "planinarenje-vodic",    ParentId = 12, SortOrder = 8 },
                new Category { Id = 180, Name = "Biciklizam — organizovane ture", Slug = "biciklizam-ture",       ParentId = 12, SortOrder = 9 },

                // ── Hrana & Piće (ParentId=13, Ids 181–188) ──────────────────
                new Category { Id = 181, Name = "Privatni kuvar / Chef",          Slug = "privatni-kuvar",        ParentId = 13, SortOrder = 1 },
                new Category { Id = 182, Name = "Priprema obroka za nedelju",     Slug = "meal-prep",             ParentId = 13, SortOrder = 2 },
                new Category { Id = 183, Name = "Dostava domaće hrane",           Slug = "dostava-domace-hrane",  ParentId = 13, SortOrder = 3 },
                new Category { Id = 184, Name = "Kurs kuvanja",                   Slug = "kurs-kuvanja",          ParentId = 13, SortOrder = 4 },
                new Category { Id = 185, Name = "Barista i priprema kafe",        Slug = "barista",               ParentId = 13, SortOrder = 5 },
                new Category { Id = 186, Name = "Izrada domaćih likera i rakija", Slug = "domaci-likeri",         ParentId = 13, SortOrder = 6 },
                new Category { Id = 187, Name = "Dekoracija torti",               Slug = "dekoracija-torti",      ParentId = 13, SortOrder = 7 },
                new Category { Id = 188, Name = "Veganski i specijalni meni",     Slug = "veganski-meni",         ParentId = 13, SortOrder = 8 }
            );
        });

        // ── ProviderProfile ────────────────────────────────────────────────
        builder.Entity<ProviderProfile>(e =>
        {
            e.Property(p => p.Profession).HasMaxLength(200).IsRequired();
            e.Property(p => p.Bio).HasMaxLength(2000);
            e.Property(p => p.Location).HasMaxLength(200).IsRequired();
            e.Property(p => p.CoverImageUrl).HasMaxLength(500);
            e.Property(p => p.Instagram).HasMaxLength(200);
            e.Property(p => p.AverageRating).HasColumnType("decimal(3,2)");
            e.HasIndex(p => p.UserId).IsUnique();
            e.HasOne(p => p.User)
             .WithOne(u => u.ProviderProfile)
             .HasForeignKey<ProviderProfile>(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── ProviderCategory (composite PK) ────────────────────────────────
        builder.Entity<ProviderCategory>(e =>
        {
            e.HasKey(pc => new { pc.ProviderProfileId, pc.CategoryId });
            e.HasOne(pc => pc.ProviderProfile)
             .WithMany(p => p.ProviderCategories)
             .HasForeignKey(pc => pc.ProviderProfileId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(pc => pc.Category)
             .WithMany(c => c.ProviderCategories)
             .HasForeignKey(pc => pc.CategoryId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Listing ────────────────────────────────────────────────────────
        builder.Entity<Listing>(e =>
        {
            e.Property(l => l.Title).HasMaxLength(300).IsRequired();
            e.Property(l => l.Description).HasMaxLength(5000).IsRequired();
            e.Property(l => l.Location).HasMaxLength(200).IsRequired();
            e.Property(l => l.PriceMode).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.FixedPrice).HasColumnType("decimal(10,2)");
            e.Property(l => l.PriceFrom).HasColumnType("decimal(10,2)");
            e.Property(l => l.PriceTo).HasColumnType("decimal(10,2)");
            e.Property(l => l.BoostScore).HasColumnType("decimal(8,2)");
            e.HasIndex(l => new { l.Status, l.IsBoosted });
            e.HasOne(l => l.ProviderProfile)
             .WithMany(p => p.Listings)
             .HasForeignKey(l => l.ProviderProfileId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Category)
             .WithMany(c => c.Listings)
             .HasForeignKey(l => l.CategoryId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── ListingImage ───────────────────────────────────────────────────
        builder.Entity<ListingImage>(e =>
        {
            e.Property(i => i.ImageUrl).HasMaxLength(500).IsRequired();
            e.HasOne(i => i.Listing)
             .WithMany(l => l.Images)
             .HasForeignKey(i => i.ListingId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Conversation ───────────────────────────────────────────────────
        builder.Entity<Conversation>(e =>
        {
            e.HasIndex(c => new { c.User1Id, c.User2Id }).IsUnique();
        });

        // ── Message ────────────────────────────────────────────────────────
        builder.Entity<Message>(e =>
        {
            e.Property(m => m.Text).HasMaxLength(4000).IsRequired();
            e.HasIndex(m => new { m.ConversationId, m.SentAt });
            e.HasOne(m => m.Conversation)
             .WithMany(c => c.Messages)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── BookingRequest ─────────────────────────────────────────────────
        builder.Entity<BookingRequest>(e =>
        {
            e.Property(b => b.Notes).HasMaxLength(2000);
            e.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(b => b.ClientId);
            e.HasIndex(b => b.ProviderUserId);
            e.HasOne(b => b.Listing)
             .WithMany(l => l.BookingRequests)
             .HasForeignKey(b => b.ListingId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── ServiceExecution ───────────────────────────────────────────────
        builder.Entity<ServiceExecution>(e =>
        {
            e.HasIndex(s => s.BookingRequestId).IsUnique();
            e.HasOne(s => s.BookingRequest)
             .WithOne(b => b.ServiceExecution)
             .HasForeignKey<ServiceExecution>(s => s.BookingRequestId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Review ─────────────────────────────────────────────────────────
        builder.Entity<Review>(e =>
        {
            e.Property(r => r.Comment).HasMaxLength(2000);
            e.HasIndex(r => new { r.ListingId, r.AuthorId }).IsUnique();
            e.HasOne(r => r.Listing)
             .WithMany(l => l.Reviews)
             .HasForeignKey(r => r.ListingId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.BookingRequest)
             .WithOne(b => b.Review)
             .HasForeignKey<Review>(r => r.BookingRequestId)
             .OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.Author)
             .WithMany(u => u.Reviews)
             .HasForeignKey(r => r.AuthorId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── TokenTransaction ───────────────────────────────────────────────
        builder.Entity<TokenTransaction>(e =>
        {
            e.Property(t => t.Amount).HasColumnType("decimal(18,2)");
            e.Property(t => t.BalanceAfter).HasColumnType("decimal(18,2)");
            e.Property(t => t.Description).HasMaxLength(500).IsRequired();
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(50);
            e.HasIndex(t => new { t.UserId, t.CreatedAt });
            e.HasOne(t => t.User)
             .WithMany(u => u.TokenTransactions)
             .HasForeignKey(t => t.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── TokenPurchase ──────────────────────────────────────────────────
        builder.Entity<TokenPurchase>(e =>
        {
            e.Property(t => t.Tokens).HasColumnType("decimal(18,2)");
            e.Property(t => t.BonusTokens).HasColumnType("decimal(18,2)");
            e.Property(t => t.AmountRsd).HasColumnType("decimal(10,2)");
            e.Property(t => t.PaymentMethod).HasMaxLength(100).IsRequired();
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(t => t.User)
             .WithMany(u => u.TokenPurchases)
             .HasForeignKey(t => t.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── ListingBoost ───────────────────────────────────────────────────
        builder.Entity<ListingBoost>(e =>
        {
            e.Property(b => b.TokensSpent).HasColumnType("decimal(18,2)");
            e.HasIndex(b => new { b.IsActive, b.ExpiresAt });
            e.HasOne(b => b.Listing)
             .WithMany(l => l.Boosts)
             .HasForeignKey(b => b.ListingId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.User)
             .WithMany(u => u.ListingBoosts)
             .HasForeignKey(b => b.UserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── DiscountTokenOffer ─────────────────────────────────────────────
        builder.Entity<DiscountTokenOffer>(e =>
        {
            e.Property(d => d.TokenAmount).HasColumnType("decimal(18,2)");
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(d => d.Sender)
             .WithMany()
             .HasForeignKey(d => d.SenderId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Receiver)
             .WithMany()
             .HasForeignKey(d => d.ReceiverId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Listing)
             .WithMany(l => l.DiscountOffers)
             .HasForeignKey(d => d.ListingId)
             .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Conversation)
             .WithMany(c => c.DiscountOffers)
             .HasForeignKey(d => d.ConversationId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Notification ───────────────────────────────────────────────────
        builder.Entity<Notification>(e =>
        {
            e.Property(n => n.Title).HasMaxLength(200).IsRequired();
            e.Property(n => n.Body).HasMaxLength(500).IsRequired();
            e.Property(n => n.ReferenceType).HasMaxLength(50);
            e.Property(n => n.Kind).HasConversion<string>().HasMaxLength(50);
            e.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
            e.HasOne(n => n.User)
             .WithMany(u => u.Notifications)
             .HasForeignKey(n => n.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── FavoriteListing ────────────────────────────────────────────────
        builder.Entity<FavoriteListing>(e =>
        {
            e.HasIndex(f => new { f.UserId, f.ListingId }).IsUnique();
            e.HasOne(f => f.User)
             .WithMany(u => u.FavoriteListings)
             .HasForeignKey(f => f.UserId)
             .OnDelete(DeleteBehavior.Cascade);   // korisnik obrisan → omiljeni brišu se
            e.HasOne(f => f.Listing)
             .WithMany()
             .HasForeignKey(f => f.ListingId)
             .OnDelete(DeleteBehavior.NoAction);  // SQL Server: nema višestruke cascade putanje
        });

        // ── FavoriteProvider ───────────────────────────────────────────────
        builder.Entity<FavoriteProvider>(e =>
        {
            e.HasIndex(f => new { f.UserId, f.ProviderProfileId }).IsUnique();
            e.HasOne(f => f.User)
             .WithMany(u => u.FavoriteProviders)
             .HasForeignKey(f => f.UserId)
             .OnDelete(DeleteBehavior.Cascade);   // korisnik obrisan → omiljeni brišu se
            e.HasOne(f => f.ProviderProfile)
             .WithMany()
             .HasForeignKey(f => f.ProviderProfileId)
             .OnDelete(DeleteBehavior.NoAction);  // SQL Server: nema višestruke cascade putanje
        });
    }
}
