using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.DTOs.Provider;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Infrastructure.Demo;

/// <summary>
/// Demo podaci za snimke ekrana, promo videe i Play listing.
///
/// ZAŠTO POSTOJI
///
/// Snimak ekrana iz produkcije bi objavio tuđe ime, sliku ili poruku. A lažni
/// oglasi i ocene U produkciji, da aplikacija „izgleda puno", su obmanjujuća
/// praksa (Zakon o zaštiti potrošača, pravila Google Play-a). Rešenje je
/// zasebno okruženje sa izmišljenim, ali smislenim podacima (Marketing 06 §3).
///
/// TRI BRANE DA SE NIKAD NE NAĐE U PRODUKCIJI
///
///   1. Radi samo uz <c>DemoSeed:Enabled=true</c> — podrazumevano isključeno.
///   2. U Production okruženju server ODBIJA da se pokrene ako je uključen
///      (<see cref="ProveriOkruzenje"/>), isto kao SecretsGuard.
///   3. Svi nalozi su na domenu <c>demo.invalid</c> — `.invalid` je po RFC 2606
///      rezervisan i nijedan mejl na njega nikad ne može stići stvarnoj osobi.
///
/// PRAVILA SADRŽAJA (Marketing 06 §3)
///
///   • obična izmišljena imena, ne poznate ličnosti ni stvarni ljudi
///   • Novi Sad: Liman, Grbavica, Detelinara, Novo naselje, Podbara
///   • opisi po šablonima iz banke tekstova, svi preko 200 znakova
///   • ocene umerene (prosek 4,6–4,9, ne svuda 5), konkretne — i SVAKA uz
///     izvršenu uslugu, jer drugačije aplikacija više ne prima
///   • fotografije samo iz foldera koji zadaš (<c>DemoSeed:ImagesPath</c>) —
///     tvoje ili licencirane; bez njih oglasi ostaju bez slika
///   • tokeni samo kao posledica događaja iz seed-a (izvršene usluge,
///     pozivnice, popust, boost), sa istim iznosima i opisima kao aplikacija
///
/// Demo nalog <see cref="KlijentEmail"/> je i klijent i uslugodavac (torte),
/// da se svi Play kadrovi — i „Moji oglasi" i novčanik — snime bez odjave.
/// </summary>
public static class DemoSeed
{
    public const string Domen = "demo.invalid";

    /// <summary>Nalog za snimke ekrana iz ugla klijenta.</summary>
    public const string KlijentEmail = "demo@" + Domen;

    public static bool JeUkljucen(IConfiguration config) =>
        config.GetValue("DemoSeed:Enabled", false);

    /// <summary>Poziva se pri startu, pre svega ostalog — vidi brana 2.</summary>
    public static void ProveriOkruzenje(IConfiguration config, IHostEnvironment env)
    {
        if (JeUkljucen(config) && env.IsProduction())
            throw new InvalidOperationException(
                "DemoSeed:Enabled je uključen u Production okruženju. Demo podaci ne smeju " +
                "u produkciju — isključi DemoSeed ili pokreni server kao Development.");
    }

    public static async Task RunAsync(IServiceProvider sp, IConfiguration config, ILogger logger)
    {
        var db = sp.GetRequiredService<AppDbContext>();

        // Idempotentno: drugi start ne pravi duplikate.
        if (await db.Users.AnyAsync(u => u.Email!.EndsWith("@" + Domen)))
        {
            logger.LogInformation("DemoSeed: podaci već postoje, preskačem.");
            return;
        }

        var lozinka = config["DemoSeed:Password"];
        if (string.IsNullOrWhiteSpace(lozinka))
            throw new InvalidOperationException(
                "DemoSeed:Password nije postavljen. Svi demo nalozi dele ovu lozinku, " +
                "da bi se za snimke moglo prijaviti i kao klijent i kao uslugodavac.");

        var users      = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var providers  = sp.GetRequiredService<ProviderService>();
        var listings   = sp.GetRequiredService<ListingService>();
        var encryption = sp.GetRequiredService<MessageEncryption>();

        // ── Klijenti ───────────────────────────────────────────────────────
        var jovana  = await NapraviKorisnikaAsync(users, db, KlijentEmail,          "Jovana Petrović",  lozinka);
        var petar   = await NapraviKorisnikaAsync(users, db, "petar.j@"  + Domen,   "Petar J.",         lozinka);
        var sanja   = await NapraviKorisnikaAsync(users, db, "sanja.m@"  + Domen,   "Sanja M.",         lozinka);
        var luka    = await NapraviKorisnikaAsync(users, db, "luka.b@"   + Domen,   "Luka B.",          lozinka);
        var tamara  = await NapraviKorisnikaAsync(users, db, "tamara.n@" + Domen,   "Tamara N.",        lozinka);
        var klijenti = new[] { jovana, petar, sanja, luka, tamara };

        // ── Uslugodavci i oglasi ───────────────────────────────────────────
        var napravljeni = new List<(Uslugodavac Opis, ApplicationUser User, int ListingId)>();

        foreach (var u in Uslugodavci)
        {
            var user = await NapraviKorisnikaAsync(users, db, u.Email, u.Ime, lozinka);
            await AktivirajAsync(providers, user, [u]);
            napravljeni.Add((u, user, await NapraviOglasAsync(listings, user, u)));
        }

        // Jovana je i uslugodavac — da se sa ISTOG naloga snimi i ekran klijenta
        // i „Moji oglasi" (Play kadar 6), bez odjave usred snimanja.
        await AktivirajAsync(providers, jovana, JovaniniOglasi);
        foreach (var u in JovaniniOglasi)
            napravljeni.Add((u, jovana, await NapraviOglasAsync(listings, jovana, u)));

        // ── Knjiga tokena ──────────────────────────────────────────────────
        // Tokeni nastaju SAMO kao posledica događaja koje seed pravi — izvršene
        // usluge, pozivnice, popust, boost — po istim pravilima i istim opisima
        // kao u aplikaciji. Balans se na kraju računa iz knjige, pa se novčanik
        // i istorija uvek slažu (vidi ZapisiKnjiguAsync).
        var knjiga            = new List<TokenTransaction>();
        var nagradaKlijentu   = config.GetValue("Booking:ServiceRewardTokens",  5m);
        var nagradaProvajderu = config.GetValue("Booking:ProviderRewardTokens", 3m);

        // ── Istaknuti oglasi (za boost na snimcima) ────────────────────────
        foreach (var (opis, _, listingId) in napravljeni.Where(n => n.Opis.BoostScore > 0))
        {
            await db.Listings.Where(l => l.Id == listingId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.IsBoosted,      true)
                .SetProperty(l => l.BoostScore,     opis.BoostScore)
                .SetProperty(l => l.BoostExpiresAt, DateTime.UtcNow.AddDays(5)));
        }

        // ── Izvršene usluge i ocene ────────────────────────────────────────
        var sada = DateTime.UtcNow;

        foreach (var (opis, provajder, listingId) in napravljeni)
        {
            // Jovana ne ocenjuje sopstvene oglase.
            var kandidati = klijenti.Where(k => k.Id != provajder.Id).ToArray();

            for (var i = 0; i < opis.Ocene.Length; i++)
            {
                var (zvezdice, komentar) = opis.Ocene[i];
                var klijent  = kandidati[(i + opis.Email.Length) % kandidati.Length];
                var kada     = sada.AddDays(-(6 + i * 9));

                var booking = new BookingRequest
                {
                    ListingId      = listingId,
                    ClientId       = klijent.Id,
                    ProviderUserId = provajder.Id,
                    RequestedDate  = DateOnly.FromDateTime(kada),
                    RequestedTime  = new TimeOnly(10 + i * 2, 0),
                    Status         = BookingStatus.Completed,
                    CreatedAt      = kada.AddDays(-3),
                    AcceptedAt     = kada.AddDays(-3),
                    UpdatedAt      = kada
                };
                db.BookingRequests.Add(booking);
                await db.SaveChangesAsync();

                db.Set<ServiceExecution>().Add(new ServiceExecution
                {
                    BookingRequestId = booking.Id,
                    ExecutedAt       = kada
                });

                db.Reviews.Add(new Review
                {
                    ListingId        = listingId,
                    BookingRequestId = booking.Id,
                    AuthorId         = klijent.Id,
                    Stars            = zvezdice,
                    Comment          = komentar,
                    CreatedAt        = kada.AddHours(5)
                });
                await db.SaveChangesAsync();

                // Isto kao BookingService.MarkExecutedAsync: obe strane dobijaju tokene.
                knjiga.Add(Transakcija(klijent.Id, nagradaKlijentu, TokenKind.ServiceReward,
                    $"Nagrada za izvršenu uslugu: {opis.Naslov}", booking.Id, kada));
                knjiga.Add(Transakcija(provajder.Id, nagradaProvajderu, TokenKind.ServiceReward,
                    $"Nagrada za označenu izvršenu uslugu: {opis.Naslov}", booking.Id, kada));
            }
        }

        // Prosek po uslugodavcu, ne po oglasu — Jovana ima dva oglasa.
        foreach (var grupa in napravljeni.GroupBy(n => n.User.Id))
        {
            var ocene  = grupa.SelectMany(n => n.Opis.Ocene).ToList();
            var profil = await db.ProviderProfiles.FirstAsync(p => p.UserId == grupa.Key);
            profil.AverageRating = Math.Round((decimal)ocene.Average(o => o.Zvezdice), 2);
            profil.TotalReviews  = ocene.Count;
        }
        await db.SaveChangesAsync();

        // ── Razgovori ──────────────────────────────────────────────────────
        var marko  = napravljeni.First(n => n.Opis.Email.StartsWith("marko")).User;
        var jelena = napravljeni.First(n => n.Opis.Email.StartsWith("jelena")).User;
        var milica = napravljeni.First(n => n.Opis.Email.StartsWith("milica")).User;

        var razgovorSaMarkom = await NapraviRazgovorAsync(db, encryption, jovana, marko, sada.AddHours(-3),
        [
            (true,  "Dobar dan, curi voda ispod sudopere. Da li ste slobodni sutra posle 16h?"),
            (false, "Dobar dan! Mogu sutra u 17h. Najverovatnije je sifon, imam rezervne kod sebe."),
            (true,  "Odlično. Liman 3, koliko otprilike izlazi?"),
            (false, "Izlazak je 2.000, a zamena sifona sa materijalom oko 3.500. Vidimo se sutra!")
        ]);

        await NapraviRazgovorAsync(db, encryption, jovana, jelena, sada.AddDays(-1),
        [
            (true,  "Zdravo, imate li slobodan termin u subotu pre podne za šišanje i feniranje?"),
            (false, "Zdravo! Subota u 10h je slobodna. Upisujem vas?"),
            (true,  "Može, hvala!")
        ]);

        // ── Token popust: Jovana → Marko, u njihovom razgovoru ─────────────
        var markovOglas = napravljeni.First(n => n.User.Id == marko.Id);
        var popust = new DiscountTokenOffer
        {
            SenderId       = jovana.Id,
            ReceiverId     = marko.Id,
            ListingId      = markovOglas.ListingId,
            ConversationId = razgovorSaMarkom,
            TokenAmount    = 5m,
            Status         = DiscountOfferStatus.Accepted,
            CreatedAt      = sada.AddHours(-2),
            RespondedAt    = sada.AddHours(-1)
        };
        db.DiscountTokenOffers.Add(popust);
        await db.SaveChangesAsync();

        // Isti opisi kao TokenWalletService.AcceptOfferAsync.
        knjiga.Add(Transakcija(jovana.Id, -popust.TokenAmount, TokenKind.DiscountSent,
            $"Token popust poslan za \"{markovOglas.Opis.Naslov}\"", popust.Id, popust.RespondedAt.Value));
        knjiga.Add(Transakcija(marko.Id, popust.TokenAmount, TokenKind.DiscountReceived,
            $"Primljeni token popust za \"{markovOglas.Opis.Naslov}\"", popust.Id, popust.RespondedAt.Value));

        // ── Pozivnice: Jovana je pozvala Tamaru i Milicu ───────────────────
        // Pravi se POSLE aktivacije uslugodavaca: inače bi ProviderService pri
        // Milicinoj aktivaciji sam isplatio drugu ratu, pa bi bila dva puta.
        var nagradaPrijava    = config.GetValue("Referral:SignupRewardTokens",             2m);
        var nagradaAktivacija = config.GetValue("Referral:ProviderActivationRewardTokens", 3m);

        var pozivTamari = new Referral
        {
            ReferrerId          = jovana.Id,
            ReferredUserId      = tamara.Id,
            ReferralCode        = jovana.ReferralCode!,
            Status              = ReferralStatus.Registered,
            CreatedAt           = sada.AddDays(-41),
            SignupTokensAwarded = nagradaPrijava,
            SignupRewardedAt    = sada.AddDays(-40)
        };
        var pozivMilici = new Referral
        {
            ReferrerId              = jovana.Id,
            ReferredUserId          = milica.Id,
            ReferralCode            = jovana.ReferralCode!,
            Status                  = ReferralStatus.Rewarded,
            CreatedAt               = sada.AddDays(-46),
            SignupTokensAwarded     = nagradaPrijava,
            SignupRewardedAt        = sada.AddDays(-45),
            ActivationTokensAwarded = nagradaAktivacija,
            ActivationRewardedAt    = sada.AddDays(-44)
        };
        db.Referrals.AddRange(pozivTamari, pozivMilici);
        await db.SaveChangesAsync();

        // Isti opisi kao ReferralService.
        knjiga.Add(Transakcija(jovana.Id, nagradaPrijava, TokenKind.Referral,
            "Referral nagrada — pozvanik je potvrdio email", pozivMilici.Id, pozivMilici.SignupRewardedAt.Value));
        knjiga.Add(Transakcija(jovana.Id, nagradaAktivacija, TokenKind.Referral,
            "Referral nagrada — pozvanik je aktivirao provajder nalog", pozivMilici.Id, pozivMilici.ActivationRewardedAt.Value));
        knjiga.Add(Transakcija(jovana.Id, nagradaPrijava, TokenKind.Referral,
            "Referral nagrada — pozvanik je potvrdio email", pozivTamari.Id, pozivTamari.SignupRewardedAt.Value));

        // ── Boost Jovaninih torti, plaćen tokenima ─────────────────────────
        // Kao BoostService: tokeni / dani = BoostScore, uz zapis u ListingBoosts.
        var torte       = napravljeni.First(n => n.Opis.KljucSlike == "torte");
        var boostOd     = sada.AddDays(-2);
        const int dana  = 7;
        const decimal potroseno = 14m;
        var boostDelta  = Math.Round(potroseno / dana, 4);

        await db.Listings.Where(l => l.Id == torte.ListingId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.IsBoosted,      true)
            .SetProperty(l => l.BoostScore,     boostDelta)
            .SetProperty(l => l.BoostExpiresAt, boostOd.AddDays(dana)));

        db.ListingBoosts.Add(new ListingBoost
        {
            ListingId    = torte.ListingId,
            UserId       = jovana.Id,
            TokensSpent  = potroseno,
            DurationDays = dana,
            StartsAt     = boostOd,
            ExpiresAt    = boostOd.AddDays(dana),
            IsActive     = true
        });
        knjiga.Add(Transakcija(jovana.Id, -potroseno, TokenKind.BoostSpend,
            $"Boost \"{torte.Opis.Naslov}\" — {dana} dana (+{boostDelta:0.####} BoostScore)", torte.ListingId, boostOd));

        await ZapisiKnjiguAsync(db, knjiga);

        // ── Slike (opciono) ────────────────────────────────────────────────
        await DodajSlikeAsync(config, listings, napravljeni, logger);

        var jovaninBalans = await db.Users.Where(u => u.Id == jovana.Id).Select(u => u.TokenBalance).FirstAsync();
        logger.LogWarning(
            "DemoSeed: napravljeno {Oglasa} oglasa i {Klijenata} klijenata na domenu {Domen}, " +
            "{Transakcija} token transakcija. Prijava za snimke: {Email} (balans {Balans} tokena, i klijent i uslugodavac)",
            napravljeni.Count, klijenti.Length, Domen, knjiga.Count, KlijentEmail, jovaninBalans);
    }

    // ── Pomoćno ────────────────────────────────────────────────────────────

    private static async Task AktivirajAsync(
        ProviderService providers, ApplicationUser user, Uslugodavac[] oglasi)
    {
        var (profil, greska) = await providers.ActivateAsync(user.Id, new ActivateProviderDto
        {
            Profession  = oglasi[0].Zanimanje,
            Bio         = oglasi[0].Bio,
            Location    = "Novi Sad",
            CategoryIds = [.. oglasi.Select(o => o.KategorijaId).Distinct()]
        });
        if (profil is null)
            throw new InvalidOperationException($"DemoSeed: aktivacija '{oglasi[0].Ime}' nije uspela: {greska}");
    }

    private static async Task<int> NapraviOglasAsync(
        ListingService listings, ApplicationUser user, Uslugodavac u)
    {
        var (oglas, greska) = await listings.CreateAsync(user.Id, new CreateListingDto
        {
            Title       = u.Naslov,
            Description = u.Opis,
            Location    = "Novi Sad",
            CategoryId  = u.KategorijaId,
            PriceMode   = u.Cena.Mode,
            FixedPrice  = u.Cena.Fiksna,
            PriceFrom   = u.Cena.Od,
            PriceTo     = u.Cena.Do
        });
        if (oglas is null)
            throw new InvalidOperationException($"DemoSeed: oglas '{u.Naslov}' nije napravljen: {greska}");

        return oglas.Id;
    }

    private static TokenTransaction Transakcija(
        string userId, decimal iznos, TokenKind vrsta, string opis, int? referenca, DateTime kada) => new()
    {
        UserId      = userId,
        Amount      = iznos,
        Kind        = vrsta,
        Description = opis,
        ReferenceId = referenca,
        CreatedAt   = kada
    };

    /// <summary>
    /// Upisuje knjigu i iz nje računa balanse: hronološki po korisniku,
    /// BalanceAfter je stanje posle svake stavke, a TokenBalance poslednje
    /// stanje. Tako novčanik i istorija ne mogu da se raziđu.
    /// </summary>
    private static async Task ZapisiKnjiguAsync(AppDbContext db, List<TokenTransaction> knjiga)
    {
        foreach (var grupa in knjiga.GroupBy(t => t.UserId))
        {
            var stanje = 0m;
            foreach (var t in grupa.OrderBy(t => t.CreatedAt))
            {
                stanje += t.Amount;
                if (stanje < 0)
                    throw new InvalidOperationException(
                        $"DemoSeed: balans korisnika {grupa.Key} pada ispod nule kod „{t.Description}\" — " +
                        "aplikacija to ne bi dozvolila, popravi redosled ili iznose u seed-u.");
                t.BalanceAfter = stanje;
            }

            var user = await db.Users.FirstAsync(u => u.Id == grupa.Key);
            user.TokenBalance = stanje;
        }

        db.TokenTransactions.AddRange(knjiga);
        await db.SaveChangesAsync();
    }

    private static async Task<ApplicationUser> NapraviKorisnikaAsync(
        UserManager<ApplicationUser> users, AppDbContext db, string email, string ime, string lozinka)
    {
        string kod;
        do { kod = TokenService.GenerateReferralCode(); }
        while (await db.Users.AnyAsync(u => u.ReferralCode == kod));

        var user = new ApplicationUser
        {
            UserName              = email,
            Email                 = email,
            FullName              = ime,
            LastKnownCity         = "Novi Sad",
            ReferralCode          = kod,
            IsActive              = true,
            EmailConfirmed        = true,
            PolicyAcceptedAt      = DateTime.UtcNow,
            PolicyVersionAccepted = PolicyVersion.Current
        };

        var rezultat = await users.CreateAsync(user, lozinka);
        if (!rezultat.Succeeded)
            throw new InvalidOperationException(
                $"DemoSeed: nalog '{email}' nije napravljen: " +
                string.Join(" ", rezultat.Errors.Select(e => e.Description)));

        await users.AddToRoleAsync(user, "User");
        return user;
    }

    private static async Task<int> NapraviRazgovorAsync(
        AppDbContext db, MessageEncryption encryption,
        ApplicationUser klijent, ApplicationUser provajder, DateTime pocetak,
        (bool OdKlijenta, string Tekst)[] poruke)
    {
        // Isto pravilo kao ConversationService: leksikografski manji id je User1.
        var (u1, u2) = string.CompareOrdinal(klijent.Id, provajder.Id) < 0
            ? (klijent.Id, provajder.Id)
            : (provajder.Id, klijent.Id);

        var razgovor = new Conversation { User1Id = u1, User2Id = u2, CreatedAt = pocetak };
        db.Conversations.Add(razgovor);
        await db.SaveChangesAsync();

        var vreme = pocetak;
        foreach (var (odKlijenta, tekst) in poruke)
        {
            vreme = vreme.AddMinutes(7);
            db.Messages.Add(new Message
            {
                ConversationId = razgovor.Id,
                SenderId       = odKlijenta ? klijent.Id : provajder.Id,
                Text           = encryption.Encrypt(tekst, razgovor.Id),
                SentAt         = vreme,
                IsRead         = true
            });
        }

        razgovor.LastMessageAt = vreme;
        await db.SaveChangesAsync();
        return razgovor.Id;
    }

    /// <summary>
    /// Slike iz <c>DemoSeed:ImagesPath</c>: fajlovi čije ime počinje ključem
    /// uslugodavca (npr. <c>vodoinstalater-1.jpg</c>, <c>vodoinstalater-2.jpg</c>).
    /// Idu kroz ISTI put kao otpremanje iz aplikacije (provera formata i sadržaja).
    /// </summary>
    private static async Task DodajSlikeAsync(
        IConfiguration config, ListingService listings,
        List<(Uslugodavac Opis, ApplicationUser User, int ListingId)> napravljeni, ILogger logger)
    {
        var folder = config["DemoSeed:ImagesPath"];
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            logger.LogInformation("DemoSeed: bez slika (DemoSeed:ImagesPath nije zadat ili ne postoji).");
            return;
        }

        string[] ekstenzije = [".jpg", ".jpeg", ".png", ".webp"];

        foreach (var (opis, user, listingId) in napravljeni)
        {
            var fajlovi = Directory.EnumerateFiles(folder)
                .Where(f => Path.GetFileName(f).StartsWith(opis.KljucSlike, StringComparison.OrdinalIgnoreCase)
                         && ekstenzije.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Order()
                .Take(5);

            foreach (var putanja in fajlovi)
            {
                await using var tok = File.OpenRead(putanja);
                var fajl = new FormFile(tok, 0, tok.Length, "file", Path.GetFileName(putanja))
                {
                    Headers     = new HeaderDictionary(),
                    ContentType = "application/octet-stream"
                };

                var (_, greska) = await listings.UploadImageAsync(listingId, user.Id, fajl);
                if (greska is not null)
                    logger.LogWarning("DemoSeed: slika {Fajl} nije prihvaćena: {Greska}", putanja, greska);
            }
        }
    }

    // ── Sadržaj ────────────────────────────────────────────────────────────

    private sealed record Cena(PriceMode Mode, decimal? Fiksna = null, decimal? Od = null, decimal? Do = null);

    private sealed record Uslugodavac(
        string Email, string Ime, string Zanimanje, string Bio, int KategorijaId,
        string Naslov, string Opis, Cena Cena, string KljucSlike, decimal BoostScore,
        (int Zvezdice, string Komentar)[] Ocene);

    // Kategorije su iz seed-a u AppDbContext (Id je stabilan, ne menja se).
    private static readonly Uslugodavac[] Uslugodavci =
    [
        new("marko.p@" + Domen, "Marko P.", "Vodoinstalater",
            "Vodoinstalater sa 12 godina iskustva. Radim ceo Novi Sad, najčešće Liman i Grbavicu.",
            71,
            "Vodoinstalater — popravke i hitne intervencije, Liman",
            "Šta radim: zamena slavina, sifona i ventila, popravka curenja, odgušenje, ugradnja bojlera " +
            "i sanitarija. Gde: Liman i ceo Novi Sad. Kada: radnim danima od 8 do 18h, subotom do 14h; " +
            "hitne intervencije po dogovoru. Cena: izlazak 2.000 din, radovi od 2.500 din, materijal po " +
            "dogovoru. Posle posla ostavljam čisto.",
            new(PriceMode.Range, Od: 2500, Do: 8000), "vodoinstalater", 12.5m,
            [(5, "Došao u dogovoreno vreme, zamenio sifon za pola sata i ostavio čisto."),
             (5, "Brzo odgovorio na poruku i rešio curenje istog dana."),
             (4, "Posao odrađen kako treba, malo je kasnio zbog gužve, ali je javio."),
             (5, "Korektna cena, unapred je rekao koliko će koštati.")]),

        new("jelena.s@" + Domen, "Jelena S.", "Frizerka",
            "Frizerka, salon na Grbavici. Svečane frizure radim i na adresi.",
            14,
            "Frizerka — šišanje, boja i svečane frizure, Grbavica",
            "Usluge: žensko šišanje, feniranje, farbanje i pramenovi, svečane frizure za venčanja i mature. " +
            "Gde: salon na Grbavici, za svečane frizure dolazim na adresu. Termini: utorak–subota, " +
            "od 9 do 19h. Cena: šišanje od 1.500 din, boja od 3.500 din — tačna cena zavisi od dužine kose. " +
            "Pogledaj galeriju radova i piši za slobodan termin.",
            new(PriceMode.Range, Od: 1500, Do: 6000), "frizerka", 4.2m,
            [(5, "Tačno ono što sam tražila, i savetovala me oko nijanse."),
             (5, "Prijatna atmosfera, termin ispoštovan u minut."),
             (4, "Lepo urađeno, samo je čekanje bilo nešto duže.")]),

        new("nikola.v@" + Domen, "Nikola V.", "Električar",
            "Licencirani električar. Instalacije, table, rasveta, otklanjanje kvarova.",
            72,
            "Električar — kvarovi, instalacije i rasveta, Detelinara",
            "Šta radim: otklanjanje kvarova, zamena utičnica i prekidača, razvodne table, nove i stare " +
            "instalacije, ugradnja rasvete i bojlera. Gde: Detelinara i ceo Novi Sad. Kada: radnim danima " +
            "od 7 do 17h, hitni kvarovi i vikendom. Cena: izlazak 2.000 din, ostalo po dogovoru posle " +
            "procene. Uvek proverim celu instalaciju pre nego što krenem.",
            new(PriceMode.Range, Od: 2000, Do: 15000), "elektricar", 0m,
            [(5, "Pronašao kvar koji dvojica pre njega nisu, sve objasnio."),
             (5, "Uredan i precizan, rasveta postavljena za jedno popodne."),
             (5, "Došao isti dan zbog hitnog kvara. Preporuka.")]),

        new("dragan.m@" + Domen, "Dragan M.", "Moler",
            "Moler i gips radovi. Krečenje stanova, gletovanje, dekorativne tehnike.",
            73,
            "Moler — krečenje i gletovanje stanova, Novo naselje",
            "Šta radim: krečenje stanova i kuća, gletovanje, popravka pukotina, gips ploče i dekorativne " +
            "tehnike. Gde: Novo naselje i ceo Novi Sad. Kada: radnim danima i subotom, po dogovoru. " +
            "Cena: krečenje od 350 din/m², gletovanje od 450 din/m², boja po izboru klijenta. Nameštaj " +
            "pokrivam i posle radova sve sklanjam.",
            new(PriceMode.Range, Od: 350, Do: 900), "moler", 0m,
            [(5, "Stan okrečen za dva dana, sve pokriveno i očišćeno posle."),
             (4, "Dobar posao, cena kao što je dogovoreno."),
             (5, "Gletovanje savršeno ravno, vidi se da zna posao.")]),

        new("ana.k@" + Domen, "Ana K.", "Profesorka matematike",
            "Profesorka matematike. Pripreme za prijemni, kontrolne i maturu.",
            155,
            "Instrukcije iz matematike — osnovna i srednja škola, Podbara",
            "Šta radim: časovi matematike za osnovnu i srednju školu, priprema za kontrolne, završni ispit, " +
            "maturu i prijemni na fakultetu. Gde: kod mene na Podbari ili onlajn. Kada: radnim danima " +
            "posle 15h, vikendom pre podne. Cena: 1.200 din za čas od 60 minuta, paket od osam časova " +
            "povoljnije. Radim polako i sa puno primera.",
            new(PriceMode.Fixed, Fiksna: 1200), "matematika", 0m,
            [(5, "Ćerka je posle mesec dana prvi put dobila peticu iz kontrolnog."),
             (5, "Strpljiva i jasna, objašnjava dok ne bude potpuno jasno."),
             (5, "Pripremila sina za prijemni, upisao se iz prvog kruga."),
             (4, "Odlični časovi, samo su termini brzo popunjeni.")]),

        new("milica.r@" + Domen, "Milica R.", "Čišćenje",
            "Generalno i redovno čišćenje stanova. Sopstvena sredstva i oprema.",
            134,
            "Čišćenje stanova — redovno i generalno, Liman",
            "Šta radim: redovno nedeljno čišćenje, generalno čišćenje, čišćenje posle krečenja i selidbe, " +
            "pranje prozora. Gde: Liman, Grbavica i centar. Kada: radnim danima od 8 do 16h. Cena: od 2.500 " +
            "din za garsonjeru, za veće stanove po kvadraturi. Donosim sopstvena sredstva i opremu, a " +
            "po želji koristim vaša.",
            new(PriceMode.Range, Od: 2500, Do: 7000), "ciscenje", 0m,
            [(5, "Stan blista, posebno kupatilo. Dolazi ponovo sledeće nedelje."),
             (4, "Temeljno i na vreme, preporuka."),
             (5, "Generalno posle krečenja, sve sređeno za jedan dan.")]),

        new("stefan.t@" + Domen, "Stefan T.", "Selidbe",
            "Selidbe i prevoz stvari kombijem. Iznošenje, unošenje, rastavljanje nameštaja.",
            137,
            "Selidbe i prevoz kombijem — Novi Sad i okolina",
            "Šta radim: selidbe stanova i kancelarija, prevoz nameštaja i bele tehnike, iznošenje i unošenje, " +
            "rastavljanje i sklapanje nameštaja. Gde: Novi Sad i okolina, po dogovoru i dalje. Kada: svakog " +
            "dana, i vikendom. Cena: od 3.000 din po turi u gradu, tačna cena zavisi od sprata i količine " +
            "stvari. Pakovanje po želji.",
            new(PriceMode.Range, Od: 3000, Do: 12000), "selidbe", 0m,
            [(5, "Brzi i pažljivi, ni jedna ogrebotina na nameštaju."),
             (4, "Sve preneto kako treba, kasnili desetak minuta."),
             (5, "Rastavili i sklopili ormar bez problema.")]),

        new("ivana.d@" + Domen, "Ivana D.", "Profesorka engleskog",
            "Profesorka engleskog jezika. Konverzacija, gramatika, priprema za ispite.",
            158,
            "Engleski jezik — časovi i konverzacija, Grbavica",
            "Šta radim: časovi engleskog za decu i odrasle, konverzacija, gramatika, priprema za IELTS i " +
            "maturu, poslovni engleski. Gde: Grbavica ili onlajn. Kada: radnim danima od 14 do 20h. " +
            "Cena: 1.300 din za čas od 60 minuta, konverzacijski čas od 45 minuta 1.000 din. Prvi čas " +
            "služi za procenu nivoa i plan rada.",
            new(PriceMode.Fixed, Fiksna: 1300), "engleski", 0m,
            [(5, "Posle tri meseca konverzacije mnogo sigurnije pričam na poslu."),
             (5, "Odlična priprema za IELTS, dobila sam ocenu koja mi je trebala."),
             (4, "Zanimljivi časovi, puno praktičnih primera.")])
    ];

    /// <summary>
    /// Oglasi demo klijentkinje Jovane — ista osoba je i uslugodavac, kao što
    /// aplikacija i dozvoljava. Zanimanje i bio uzimaju se iz prvog oglasa.
    /// BoostScore je 0 jer se boost torti pravi posebno, plaćen tokenima.
    /// </summary>
    private static readonly Uslugodavac[] JovaniniOglasi =
    [
        new(KlijentEmail, "Jovana Petrović", "Poslastičarka",
            "Torte i kolači po porudžbini iz moje kuhinje na Limanu. Domaći sastojci, bez gotovih smesa.",
            104,
            "Torte po porudžbini — rođendani i slavlja, Liman",
            "Šta pravim: rođendanske i dečje torte, torte za slave, krštenja i venčanja, voćne i čokoladne. " +
            "Gde: preuzimanje na Limanu, dostava po Novom Sadu po dogovoru. Kada: porudžbine najkasnije " +
            "pet dana unapred, za venčanja dve nedelje. Cena: od 3.500 din za tortu od 1,5 kg, tačna cena " +
            "zavisi od veličine i ukrasa. Pošalji fotografiju ili ideju u poruci.",
            new(PriceMode.Range, Od: 3500, Do: 9000), "torte", 0m,
            [(5, "Torta za ćerkin rođendan je bila i lepa i ukusna, deca su oduševljena."),
             (5, "Švarcvald kao kod bake, stigla tačno u dogovoreno vreme.")]),

        new(KlijentEmail, "Jovana Petrović", "Poslastičarka",
            "Torte i kolači po porudžbini iz moje kuhinje na Limanu. Domaći sastojci, bez gotovih smesa.",
            187,
            "Dekoracija torti — figurice, cveće i natpisi po želji",
            "Šta radim: dekoracija torti fondanom i šlagom, šećerno cveće, figurice, natpisi i jestive " +
            "slike, tematske torte za dečje rođendane. Gde: Liman, preuzimanje ili dostava po dogovoru. " +
            "Kada: najkasnije nedelju dana unapred. Cena: dekoracija od 1.500 din, figurice po komadu. " +
            "Mogu da ukrasim i tortu koju ste sami ispekli.",
            new(PriceMode.Range, Od: 1500, Do: 5000), "dekoracija", 0m,
            [(5, "Figurice za tematski rođendan baš kao na slici koju sam poslala."),
             (4, "Lepo urađeno cveće na torti za krštenje, malo duže čekanje.")])
    ];
}
