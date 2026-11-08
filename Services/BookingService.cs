using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Admin;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Booking modul — upravlja celim životnim ciklusom booking zahteva:
///   Pending → Confirmed → Completed (token nagrada)
///   Pending → Rejected
///   Pending → Cancelled (klijent otkazuje)
///
/// Pravilo čekanja: provider može označiti uslugu kao izvršenu tek
/// Booking:ExecuteAfterDays dana (podrazumevano 7) nakon potvrde
/// (AcceptedAt + ExecuteAfterDays &lt; UtcNow). Ovo sprečava farmovanje tokena:
/// dva naloga ne mogu za par minuta da „obave" uslugu i pokupe nagradu.
/// Kad čekanje istekne, uslugodavac dobija obaveštenje (PodsetnikZaIzvrsenje).
///
/// Izuzetak: admin može potvrđenom terminu da skine čekanje (pouzdani
/// korisnici), a skida se i samo, pri potvrdi, kad su oba učesnika označena
/// kao pouzdana. Tada BookingRequest.ExecuteAllowedAt zamenjuje
/// AcceptedAt + ExecuteAfterDays. Rok se računa SAMO u
/// <see cref="IzvrsenjeDozvoljenoOd"/> — server, DTO za aplikaciju i podsetnik
/// čitaju isto pravilo. Nagrade i ocene se ne menjaju, samo trenutak od kog je
/// „Izvršeno" dozvoljeno.
/// </summary>
public sealed class BookingService(
    AppDbContext         db,
    IConfiguration       config,
    NotificationService  notificationService,
    BlockService         blockService,
    ConversationService  conversationService,
    ILogger<BookingService> logger)
{
    // ── NAGRADE ZA IZVRŠENU USLUGU ─────────────────────────────────────────
    // Obe strane dobijaju tokene, i to namerno različito.
    //
    // Klijent dobija više (5) jer je on taj koji tokene i troši — na popuste i
    // na isticanje sopstvenih oglasa. To mu je razlog da posao dogovara kroz
    // aplikaciju umesto telefonom.
    //
    // Uslugodavac dobija manje (3), ali dobija ga ZA SAM ČIN OZNAČAVANJA. Bez
    // toga je označavanje izvršene usluge čist trošak vremena bez ikakve
    // koristi po njega, pa se u praksi ne radi — a dok booking stoji na
    // „Potvrđeno", klijent ne može da ostavi recenziju. Nagrada za uslugodavca
    // zato ne kupuje samo njegovu pažnju nego otključava i recenzije, koje su
    // ono što celu platformu drži.
    private decimal ServiceRewardTokens =>
        config.GetValue<decimal>("Booking:ServiceRewardTokens", 5m);

    private decimal ProviderRewardTokens =>
        config.GetValue<decimal>("Booking:ProviderRewardTokens", 3m);

    public int ExecuteAfterDays =>
        config.GetValue<int>("Booking:ExecuteAfterDays", 7);

    /// <summary>
    /// Od kog trenutka (UTC) uslugodavac sme da označi uslugu kao izvršenu.
    /// Skinuto čekanje (<see cref="BookingRequest.ExecuteAllowedAt"/>) ima
    /// prednost nad AcceptedAt + ExecuteAfterDays. Null dok zahtev nije prihvaćen.
    /// </summary>
    public DateTime? IzvrsenjeDozvoljenoOd(BookingRequest b) =>
        b.ExecuteAllowedAt ?? b.AcceptedAt?.AddDays(ExecuteAfterDays);

    /// <summary>Da li je „Izvršeno" sada dozvoljeno (status + rok).</summary>
    public bool MozeSeIzvrsiti(BookingRequest b, DateTime sada) =>
        b.Status == BookingStatus.Confirmed
        && IzvrsenjeDozvoljenoOd(b) is DateTime od
        && sada >= od;

    // ── CREATE ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Klijent šalje booking zahtev za listing.
    /// Uslovi: email verifikovan, listing aktivan, ne sopstveni listing,
    /// nema već aktivnog zahteva za isti listing.
    /// </summary>
    public async Task<(BookingDto?, string?)> CreateAsync(string clientId, CreateBookingDto dto)
    {
        var client = await db.Users.FindAsync(clientId);
        if (client is null)
            return (null, "Korisnik nije pronađen.");

        // Anti-farming: email mora biti verifikovan
        if (!client.EmailConfirmed)
            return (null, "Email adresa mora biti verifikovana pre slanja zahteva za uslugu.");

        // Listing mora biti aktivan i imati providera
        var listing = await db.Listings
            .AsNoTracking()
            .Include(l => l.ProviderProfile).ThenInclude(p => p.User)
            .FirstOrDefaultAsync(l => l.Id == dto.ListingId && l.Status == ListingStatus.Active);

        if (listing is null)
            return (null, "Listing nije pronađen ili nije aktivan.");

        var providerUserId = listing.ProviderProfile.UserId;

        // Korisnik ne može bookirati sopstveni listing
        if (providerUserId == clientId)
            return (null, "Ne možete poslati zahtev za sopstveni oglas.");

        // Blokada zatvara i rezervaciju.
        //
        // Zahtev nosi poruku i okida notifikaciju uslugodavcu, pa bi bez ove
        // provere bio zaobilazni kanal za kontakt — tačno ono što blokada treba
        // da spreči.
        //
        // Već POTVRĐENE rezervacije se namerno ne diraju: posao je dogovoren i
        // možda plaćen, a blokada nije osnov da se ta obaveza poništi.
        if (await blockService.JeBlokiranoAsync(clientId, providerUserId))
            return (null, "Rezervacija kod ovog uslugodavca nije moguća.");

        // Nema duplikata — samo jedan aktivan zahtev po listingu
        var existingActive = await db.BookingRequests.AnyAsync(b =>
            b.ClientId  == clientId          &&
            b.ListingId == dto.ListingId     &&
            (b.Status   == BookingStatus.Pending || b.Status == BookingStatus.Confirmed));

        if (existingActive)
            return (null, "Već imate aktivan zahtev za ovaj oglas.");

        var now = DateTime.UtcNow;

        var booking = new BookingRequest
        {
            ListingId      = dto.ListingId,
            ClientId       = clientId,
            ProviderUserId = providerUserId,
            Notes          = dto.Notes?.Trim(),
            Status         = BookingStatus.Pending,
            // RequestedDate/Time: placeholder vrednosti — scheduling stiže u Business planu
            RequestedDate  = DateOnly.FromDateTime(now),
            RequestedTime  = TimeOnly.FromDateTime(now),
            CreatedAt      = now,
            UpdatedAt      = now
        };

        db.BookingRequests.Add(booking);
        await db.SaveChangesAsync(); // save da dobijemo booking.Id

        // Razgovor se otvara ZAJEDNO sa zahtevom, kod oba korisnika.
        //
        // Zahtev za uslugu skoro uvek povlači pitanje — kad, gde, koliko tačno.
        // Ranije je uslugodavac dobijao obaveštenje o zahtevu, ali razgovora
        // nije bilo dok ga neko ručno ne otvori preko „Poruka" na oglasu, pa je
        // propratna poruka tražila zaobilazan put kroz stranicu oglasa.
        //
        // Pošto je razgovor zajednički red u bazi, dovoljno je napraviti ga
        // jednom — pojavljuje se na listi kod oboje.
        await conversationService.EnsureExistsAsync(clientId, providerUserId);

        await notificationService.SendAsync(
            providerUserId,
            NotificationKind.BookingReceived,
            "Novi zahtev za uslugu",
            $"{client.FullName} šalje zahtev za „{listing.Title}“.",
            booking.Id);

        logger.LogInformation(
            "Booking #{Id} kreiran: klijent={ClientId} → listing={ListingId}",
            booking.Id, clientId, dto.ListingId);

        // Dopuni navigacije za mapping (već su u memoriji)
        booking.Listing  = listing;
        booking.Client   = client;
        booking.Provider = listing.ProviderProfile.User;

        return (MapToDto(booking), null);
    }

    // ── INCOMING (provider view) ────────────────────────────────────────────
    /// <summary>Svi zahtevi koji su poslati ovom provideru, od najnovijeg.</summary>
    public async Task<List<BookingDto>> GetIncomingAsync(string providerUserId)
    {
        var bookings = await db.BookingRequests
            .AsNoTracking()
            .Include(b => b.Client)
            .Include(b => b.Provider)
            .Include(b => b.Listing)
            .Where(b => b.ProviderUserId == providerUserId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return bookings.Select(MapToDto).ToList();
    }

    // ── OUTGOING (client view) ──────────────────────────────────────────────
    /// <summary>Svi zahtevi koje je ovaj klijent poslao, od najnovijeg.</summary>
    public async Task<List<BookingDto>> GetOutgoingAsync(string clientId)
    {
        var bookings = await db.BookingRequests
            .AsNoTracking()
            .Include(b => b.Client)
            .Include(b => b.Provider)
            .Include(b => b.Listing)
            .Where(b => b.ClientId == clientId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return bookings.Select(MapToDto).ToList();
    }

    // ── CONFIRM ────────────────────────────────────────────────────────────
    /// <summary>
    /// Provider potvrđuje zahtev.
    /// Postavlja AcceptedAt = UtcNow — startuje 3-dnevni timer.
    /// </summary>
    public async Task<(bool, string?)> ConfirmAsync(int bookingId, string providerUserId)
    {
        var booking = await db.BookingRequests
            .Include(b => b.Listing)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.ProviderUserId == providerUserId);

        if (booking is null)
            return (false, "Zahtev nije pronađen.");

        if (booking.Status != BookingStatus.Pending)
            return (false, "Zahtev nije u statusu čekanja.");

        var now = DateTime.UtcNow;
        booking.Status     = BookingStatus.Confirmed;
        booking.AcceptedAt = now;
        booking.UpdatedAt  = now;

        // Oba učesnika pouzdana → čekanje se preskače odmah pri potvrdi.
        // Podsetnik (PodsetnikZaIzvrsenje) zatim javi uslugodavcu da može da
        // označi izvršenje, kao i kad čekanje istekne.
        var pouzdanih = await db.Users.CountAsync(u =>
            (u.Id == booking.ClientId || u.Id == booking.ProviderUserId) && u.IsTrusted);
        if (pouzdanih == 2)
        {
            booking.ExecuteAllowedAt   = now;
            booking.ExecuteAllowedById = null;
            logger.LogInformation(
                "Booking #{Id}: oba učesnika pouzdana — čekanje do izvršenja preskočeno.", bookingId);
        }

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            booking.ClientId,
            NotificationKind.BookingConfirmed,
            "Zahtev je prihvaćen",
            $"Vaš zahtev za „{booking.Listing.Title}“ je prihvaćen.",
            booking.Id);

        logger.LogInformation("Booking #{Id} potvrđen od providera {ProviderId}", bookingId, providerUserId);
        return (true, null);
    }

    // ── REJECT ─────────────────────────────────────────────────────────────
    /// <summary>Provider odbija zahtev. Dozvoljeno dok je Pending ili Confirmed
    /// (provider se može predomisliti i nakon prihvatanja, sve dok nije izvršen).</summary>
    public async Task<(bool, string?)> RejectAsync(int bookingId, string providerUserId)
    {
        var booking = await db.BookingRequests
            .Include(b => b.Listing)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.ProviderUserId == providerUserId);

        if (booking is null)
            return (false, "Zahtev nije pronađen.");

        if (booking.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            return (false, "Zahtev se ne može odbiti u trenutnom statusu.");

        var now = DateTime.UtcNow;
        booking.Status    = BookingStatus.Rejected;
        booking.UpdatedAt = now;

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            booking.ClientId,
            NotificationKind.BookingRejected,
            "Zahtev je odbijen",
            $"Vaš zahtev za „{booking.Listing.Title}“ je odbijen.",
            booking.Id);

        logger.LogInformation("Booking #{Id} odbijen od providera {ProviderId}", bookingId, providerUserId);
        return (true, null);
    }

    // ── CANCEL ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Klijent otkazuje zahtev — dostupno samo dok je Status = Pending.
    /// Provajder dobija notifikaciju.
    /// </summary>
    public async Task<(bool, string?)> CancelAsync(int bookingId, string clientId)
    {
        var booking = await db.BookingRequests
            .Include(b => b.Listing)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.ClientId == clientId);

        if (booking is null)
            return (false, "Zahtev nije pronađen.");

        if (booking.Status != BookingStatus.Pending)
            return (false, "Zahtev se može otkazati samo dok je u statusu čekanja.");

        var now = DateTime.UtcNow;
        booking.Status    = BookingStatus.Cancelled;
        booking.UpdatedAt = now;

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            booking.ProviderUserId,
            NotificationKind.BookingCancelled,
            "Zahtev je otkazan",
            $"Klijent je otkazao zahtev za „{booking.Listing.Title}“.",
            booking.Id);

        logger.LogInformation("Booking #{Id} otkazan od klijenta {ClientId}", bookingId, clientId);
        return (true, null);
    }

    // ── EXECUTE ────────────────────────────────────────────────────────────
    /// <summary>
    /// Provider označava uslugu kao izvršenu.
    ///
    /// Uslovi:
    ///   1. Booking mora biti Confirmed
    ///   2. Mora proći ExecuteAfterDays dana od AcceptedAt (default: 7),
    ///      osim ako je čekanje skinuto (vidi IzvrsenjeDozvoljenoOd)
    ///
    /// Efekti:
    ///   - Kreira ServiceExecution zapis
    ///   - Booking.Status → Completed
    ///   - Klijent dobija ServiceRewardTokens tokena
    ///   - Kreira TokenTransaction i Notification za klijenta
    /// </summary>
    public async Task<(BookingDto?, string?)> ExecuteAsync(int bookingId, string providerUserId)
    {
        var booking = await db.BookingRequests
            .Include(b => b.Client)
            .Include(b => b.Provider)
            .Include(b => b.Listing)
            .Include(b => b.ServiceExecution)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.ProviderUserId == providerUserId);

        if (booking is null)
            return (null, "Zahtev nije pronađen.");

        if (booking.Status != BookingStatus.Confirmed)
            return (null, "Usluga se može označiti kao izvršena samo iz statusa 'Potvrđeno'.");

        if (IzvrsenjeDozvoljenoOd(booking) is not DateTime dozvoljenoOd)
            return (null, "Booking nema datum prihvatanja.");

        var ostalo = dozvoljenoOd - DateTime.UtcNow;
        if (ostalo > TimeSpan.Zero)
        {
            var daysLeft = (int)Math.Ceiling(ostalo.TotalDays);
            return (null, $"Usluga se može označiti kao izvršena tek za {daysLeft} dan(a) " +
                          $"({ExecuteAfterDays} dana nakon potvrde).");
        }

        // Idempotentnost: ako već postoji ServiceExecution, samo vrati DTO
        if (booking.ServiceExecution is not null)
            return (MapToDto(booking), null);

        var now = DateTime.UtcNow;

        // Promeni status
        booking.Status    = BookingStatus.Completed;
        booking.UpdatedAt = now;

        // Kreiraj ServiceExecution
        var execution = new ServiceExecution
        {
            BookingRequestId = booking.Id,
            ExecutedAt       = now
        };
        db.ServiceExecutions.Add(execution);

        await db.SaveChangesAsync(); // da dobijemo execution.Id

        // ── Nagrade ──────────────────────────────────────────────────────────
        // Obe transakcije se upisuju u istom SaveChanges-u kao i balansi, pa je
        // nemoguće da balans poraste a trag o tome izostane (ili obrnuto).
        var rewardAmount   = ServiceRewardTokens;
        var providerReward = ProviderRewardTokens;
        var client         = booking.Client;
        var provider       = booking.Provider;

        client.TokenBalance += rewardAmount;

        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = booking.ClientId,
            Amount       = rewardAmount,
            Kind         = TokenKind.ServiceReward,
            Description  = $"Nagrada za izvršenu uslugu: {booking.Listing.Title}",
            ReferenceId  = booking.Id,
            BalanceAfter = client.TokenBalance,
            CreatedAt    = now
        });

        // Uslugodavac se nagrađuje što je uslugu označio kao izvršenu — vidi
        // obrazloženje uz ProviderRewardTokens.
        if (providerReward > 0 && provider is not null)
        {
            provider.TokenBalance += providerReward;

            db.TokenTransactions.Add(new TokenTransaction
            {
                UserId       = booking.ProviderUserId,
                Amount       = providerReward,
                Kind         = TokenKind.ServiceReward,
                Description  = $"Nagrada za označenu izvršenu uslugu: {booking.Listing.Title}",
                ReferenceId  = booking.Id,
                BalanceAfter = provider.TokenBalance,
                CreatedAt    = now
            });
        }

        await db.SaveChangesAsync();

        // Obaveštenje klijentu nosi DVE stvari: tokene i poziv na ocenu.
        //
        // Spojene su u jedno namerno. Dva obaveštenja jedno za drugim za isti
        // događaj čitaju se kao greška aplikacije, a poziv na ocenu bez povoda
        // deluje nametljivo — ovako povod stoji u istoj rečenici.
        //
        // ReferenceId je ID BOOKING-a, ne izvršenja: klijent sa tog ekrana ide
        // na recenziju, a recenzija se vezuje za booking (vidi
        // ReviewService.CreateAsync). Izvršenje nema svoju stranicu.
        await notificationService.SendAsync(
            booking.ClientId,
            NotificationKind.TokenEarned,
            "Usluga je izvršena — zaradili ste tokene",
            $"Dobili ste {rewardAmount:0.##} tokena za uslugu „{booking.Listing.Title}“. " +
            "Ocenite uslugu da drugi znaju kako je prošlo.",
            booking.Id);

        logger.LogInformation(
            "Booking #{Id} izvršen — klijent {ClientId} +{Amount} tokena (balans {Balance}), " +
            "uslugodavac {ProviderId} +{ProviderAmount}",
            bookingId, booking.ClientId, rewardAmount, client.TokenBalance,
            booking.ProviderUserId, providerReward);

        booking.ServiceExecution = execution;
        return (MapToDto(booking), null);
    }

    // ── ADMIN: SKIDANJE ČEKANJA ────────────────────────────────────────────
    /// <summary>
    /// Potvrđeni termini za admin ekran „Čekanje izvršenja", od najskorije
    /// potvrđenih. <paramref name="samoNaCekanju"/> ostavlja samo one kojima
    /// „Izvršeno" još nije dozvoljeno.
    /// </summary>
    public async Task<(List<AdminBookingDto> Items, int Total)> GetConfirmedForAdminAsync(
        int page, int pageSize, bool samoNaCekanju)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(page, 1);

        var sada    = DateTime.UtcNow;
        var granica = sada.AddDays(-ExecuteAfterDays);

        var query = db.BookingRequests.AsNoTracking()
            .Where(b => b.Status == BookingStatus.Confirmed);

        // Isto pravilo kao IzvrsenjeDozvoljenoOd, prevedeno u SQL (skidanje
        // je uvek „sada", pa skinuto čekanje znači da je već dozvoljeno).
        if (samoNaCekanju)
            query = query.Where(b => b.ExecuteAllowedAt == null && b.AcceptedAt > granica);

        var total = await query.CountAsync();

        var redovi = await query
            .OrderByDescending(b => b.AcceptedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(b => b.Listing)
            .Include(b => b.Client)
            .Include(b => b.Provider)
            .ToListAsync();

        var adminIds = redovi.Select(b => b.ExecuteAllowedById).OfType<string>().Distinct().ToList();
        var admini   = adminIds.Count == 0
            ? new Dictionary<string, string>()
            : await db.Users.AsNoTracking()
                .Where(u => adminIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var items = redovi.Select(b => new AdminBookingDto
        {
            Id                   = b.Id,
            ListingTitle         = b.Listing.Title,
            ClientId             = b.ClientId,
            ClientName           = b.Client.FullName,
            ClientTrusted        = b.Client.IsTrusted,
            ProviderUserId       = b.ProviderUserId,
            ProviderName         = b.Provider.FullName,
            ProviderTrusted      = b.Provider.IsTrusted,
            AcceptedAt           = b.AcceptedAt,
            CanExecuteAt         = IzvrsenjeDozvoljenoOd(b),
            CanExecute           = MozeSeIzvrsiti(b, sada),
            ExecuteAllowedAt     = b.ExecuteAllowedAt,
            ExecuteAllowedByName = b.ExecuteAllowedById is string id
                ? admini.GetValueOrDefault(id, "obrisan nalog")
                : null
        }).ToList();

        return (items, total);
    }

    /// <summary>
    /// Admin skida čekanje: „Izvršeno" je dozvoljeno odmah. Termin mora biti
    /// potvrđen i još na čekanju. Beleži se ko je i kada, a uslugodavac odmah
    /// dobija obaveštenje (ono ujedno zamenjuje podsetnik — šalje se jednom).
    /// </summary>
    public async Task<(bool Ok, string? Greska, bool NePostoji)> SkiniCekanjeAsync(
        int bookingId, string adminId)
    {
        var booking = await db.BookingRequests
            .Include(b => b.Listing)
            .Include(b => b.Client)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking is null)
            return (false, "Termin nije pronađen.", true);

        if (booking.Status != BookingStatus.Confirmed)
            return (false, "Čekanje se može skinuti samo potvrđenom terminu.", false);

        var sada = DateTime.UtcNow;
        if (MozeSeIzvrsiti(booking, sada))
            return (false, "Čekanje je već isteklo — uslugodavac već može da označi izvršenje.", false);

        booking.ExecuteAllowedAt   = sada;
        booking.ExecuteAllowedById = adminId;
        booking.UpdatedAt          = sada;
        await db.SaveChangesAsync();

        // Isti tip kao podsetnik, pa ga PodsetnikZaIzvrsenje posle ne ponavlja.
        await notificationService.SendAsync(
            booking.ProviderUserId,
            NotificationKind.BookingExecutable,
            "Uslugu možeš označiti kao izvršenu",
            PodsetnikZaIzvrsenje.TekstOdmah(booking.Listing.Title, booking.Client.FullName),
            booking.Id);

        logger.LogInformation(
            "Booking #{Id}: admin {AdminId} skinuo čekanje do izvršenja.", bookingId, adminId);
        return (true, null, false);
    }

    /// <summary>
    /// Vraća podrazumevano čekanje (AcceptedAt + ExecuteAfterDays). Ako je
    /// uslugodavac već dobio obaveštenje „možeš označiti", ono se ne povlači,
    /// a podsetnik se posle isteka čekanja ne šalje ponovo.
    /// </summary>
    public async Task<(bool Ok, string? Greska, bool NePostoji)> VratiCekanjeAsync(
        int bookingId, string adminId)
    {
        var booking = await db.BookingRequests.FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking is null)
            return (false, "Termin nije pronađen.", true);

        if (booking.Status != BookingStatus.Confirmed)
            return (false, "Čekanje se može menjati samo potvrđenom terminu.", false);

        if (booking.ExecuteAllowedAt is null)
            return (false, "Termin već ima podrazumevano čekanje.", false);

        booking.ExecuteAllowedAt   = null;
        booking.ExecuteAllowedById = null;
        booking.UpdatedAt          = DateTime.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Booking #{Id}: admin {AdminId} vratio podrazumevano čekanje do izvršenja.", bookingId, adminId);
        return (true, null, false);
    }

    // ── HELPER ─────────────────────────────────────────────────────────────
    private BookingDto MapToDto(BookingRequest b) => new()
    {
        Id             = b.Id,
        ListingId      = b.ListingId,
        ListingTitle   = b.Listing?.Title     ?? string.Empty,
        PriceMode      = b.Listing?.PriceMode.ToString() ?? string.Empty,
        FixedPrice     = b.Listing?.FixedPrice,
        PriceFrom      = b.Listing?.PriceFrom,
        PriceTo        = b.Listing?.PriceTo,
        ClientId       = b.ClientId,
        ClientName     = b.Client?.FullName   ?? string.Empty,
        ClientImageUrl = b.Client?.ProfileImageUrl,
        ClientRating      = b.Client?.ClientRating ?? 0,
        ClientReviewCount = b.Client?.ClientReviewCount ?? 0,
        ProviderUserId = b.ProviderUserId,
        ProviderName   = b.Provider?.FullName ?? string.Empty,
        Notes          = b.Notes,
        Status         = b.Status.ToString(),
        CreatedAt      = b.CreatedAt,
        AcceptedAt     = b.AcceptedAt,
        ExecuteAfterDays = ExecuteAfterDays,
        CanExecuteAt   = IzvrsenjeDozvoljenoOd(b),
        CanExecute     = MozeSeIzvrsiti(b, DateTime.UtcNow),
        ExecuteWaitWaived = b.ExecuteAllowedAt.HasValue
    };
}
