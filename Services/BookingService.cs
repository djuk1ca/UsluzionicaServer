using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Bookings;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Booking modul — upravlja celim životnim ciklusom booking zahteva:
///   Pending → Confirmed → Completed (token nagrada)
///   Pending → Rejected
///   Pending → Cancelled (klijent otkazuje)
///
/// 3-dnevno pravilo: provider može označiti uslugu kao izvršenu
/// tek 3 dana nakon potvrde (AcceptedAt + ExecuteAfterDays &lt; UtcNow).
/// Ovo sprečava prevremeno farmovanje tokena.
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

    private int ExecuteAfterDays =>
        config.GetValue<int>("Booking:ExecuteAfterDays", 3);

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
            return (null, "Email adresa mora biti verifikovana pre slanja booking zahteva.");

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
            return (null, "Ne možete poslati booking zahtev za sopstveni listing.");

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
            return (null, "Već imate aktivan booking zahtev za ovaj listing.");

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
            "Novi booking zahtev",
            $"{client.FullName} je poslao/la zahtev za \"{listing.Title}\"",
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

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            booking.ClientId,
            NotificationKind.BookingConfirmed,
            "Booking potvrđen!",
            $"Vaš zahtev za \"{booking.Listing.Title}\" je potvrđen.",
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
            "Booking odbijen",
            $"Vaš zahtev za \"{booking.Listing.Title}\" je odbijen.",
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
            "Booking otkazan",
            $"Zahtev za \"{booking.Listing.Title}\" je otkazan od strane klijenta.",
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
    ///   2. Mora proći ExecuteAfterDays dana od AcceptedAt (default: 3)
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

        if (!booking.AcceptedAt.HasValue)
            return (null, "Booking nema datum prihvatanja.");

        var daysSinceAccepted = (DateTime.UtcNow - booking.AcceptedAt.Value).TotalDays;
        if (daysSinceAccepted < ExecuteAfterDays)
        {
            var daysLeft = (int)Math.Ceiling(ExecuteAfterDays - daysSinceAccepted);
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
            $"Dobili ste {rewardAmount:0.##} tokena za uslugu \"{booking.Listing.Title}\". " +
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

    // ── NEOCENJENE IZVRŠENE USLUGE ─────────────────────────────────────────
    /// <summary>
    /// Izvršene usluge klijenta za koje još nije ostavio recenziju.
    ///
    /// Postojanje recenzije se proverava po PARU (autor, oglas), a ne po
    /// booking-u. Recenzija sme da se ostavi i bez veze sa booking-om
    /// (<c>BookingRequestId</c> je opciono u <c>CreateReviewDto</c>), pa bi
    /// provera po booking-u prijavila kao neocenjenu i uslugu koju je klijent
    /// upravo ocenio sa stranice oglasa. Isti kriterijum koristi i lista
    /// „Moje rezervacije" kad odlučuje da li da ponudi dugme za ocenu.
    /// </summary>
    public async Task<List<PendingReviewDto>> GetPendingReviewsAsync(string clientId)
    {
        return await db.BookingRequests
            .AsNoTracking()
            .Where(b => b.ClientId == clientId
                     && b.Status   == BookingStatus.Completed
                     && !db.Reviews.Any(r => r.AuthorId  == clientId
                                          && r.ListingId == b.ListingId))
            .OrderByDescending(b => b.UpdatedAt)
            .Select(b => new PendingReviewDto
            {
                BookingId    = b.Id,
                ListingId    = b.ListingId,
                ListingTitle = b.Listing.Title,
                ProviderName = b.Provider.FullName,
                CompletedAt  = b.UpdatedAt
            })
            .ToListAsync();
    }

    // ── HELPER ─────────────────────────────────────────────────────────────
    private BookingDto MapToDto(BookingRequest b) => new()
    {
        Id             = b.Id,
        ListingId      = b.ListingId,
        ListingTitle   = b.Listing?.Title     ?? string.Empty,
        ClientId       = b.ClientId,
        ClientName     = b.Client?.FullName   ?? string.Empty,
        ClientImageUrl = b.Client?.ProfileImageUrl,
        ProviderUserId = b.ProviderUserId,
        ProviderName   = b.Provider?.FullName ?? string.Empty,
        Notes          = b.Notes,
        Status         = b.Status.ToString(),
        CreatedAt      = b.CreatedAt,
        AcceptedAt     = b.AcceptedAt,
        CanExecute     = b.Status == BookingStatus.Confirmed
                      && b.AcceptedAt.HasValue
                      && (DateTime.UtcNow - b.AcceptedAt.Value).TotalDays >= ExecuteAfterDays
    };
}
