using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.DiscountOffers;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.DTOs.Referrals;
using UsluzionicaServer.DTOs.Tokens;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Token modul: wallet (balans + ledger), discount token ponude, referral statistika.
/// </summary>
public sealed class TokenWalletService(
    AppDbContext db,
    IConfiguration config,
    NotificationService notificationService,
    ILogger<TokenWalletService> logger)
{
    // ── WALLET ─────────────────────────────────────────────────────────────

    /// <summary>Trenutni token balans prijavljenog korisnika.</summary>
    public async Task<BalanceDto?> GetBalanceAsync(string userId)
    {
        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new BalanceDto { UserId = u.Id, Balance = u.TokenBalance })
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Paginovani ledger svih token transakcija korisnika.
    /// Sortiran od najnovije.
    /// </summary>
    public async Task<PagedResult<TransactionDto>> GetTransactionsAsync(
        string userId, int page, int pageSize)
    {
        pageSize = Math.Clamp(pageSize, 1, 50);
        page     = Math.Max(page, 1);

        var query = db.TokenTransactions
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TransactionDto
            {
                Id          = t.Id,
                Amount      = t.Amount,
                Kind        = t.Kind.ToString(),
                Description = t.Description,
                BalanceAfter = t.BalanceAfter,
                CreatedAt   = t.CreatedAt,
                ReferenceId = t.ReferenceId
            })
            .ToListAsync();

        return new PagedResult<TransactionDto>
        {
            Items    = items,
            Total    = total,
            Page     = page,
            PageSize = pageSize
        };
    }

    // ── DISCOUNT OFFERS ────────────────────────────────────────────────────

    /// <summary>
    /// Klijent šalje token ponudu provideru za određeni listing.
    /// Tokeni se NE rezervišu odmah — proveravaju se pri prihvatanju.
    /// </summary>
    public async Task<(DiscountOfferDto?, string?)> CreateOfferAsync(
        string senderId, CreateDiscountOfferDto dto)
    {
        if (dto.ReceiverId == senderId)
            return (null, "Ne možete slati ponudu samome sebi.");

        // Osnovna provera balansa (nije atomična — precizna provera je pri accept)
        var sender = await db.Users.FindAsync(senderId);
        if (sender is null) return (null, "Korisnik nije pronađen.");

        if (sender.TokenBalance < dto.TokenAmount)
            return (null, $"Nedovoljno tokena. Vaš balans: {sender.TokenBalance:0.##}, " +
                          $"potrebno: {dto.TokenAmount:0.##}.");

        var listing = await db.Listings.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == dto.ListingId && l.Status == ListingStatus.Active);
        if (listing is null)
            return (null, "Listing nije pronađen ili nije aktivan.");

        var receiver = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == dto.ReceiverId);
        if (receiver is null)
            return (null, "Primalac nije pronađen.");

        var now   = DateTime.UtcNow;
        var offer = new DiscountTokenOffer
        {
            SenderId       = senderId,
            ReceiverId     = dto.ReceiverId,
            ListingId      = dto.ListingId,
            ConversationId = dto.ConversationId,
            TokenAmount    = dto.TokenAmount,
            Status         = DiscountOfferStatus.Pending,
            CreatedAt      = now
        };

        db.DiscountTokenOffers.Add(offer);
        await db.SaveChangesAsync(); // potreban offer.Id za notifikaciju

        await notificationService.SendAsync(
            dto.ReceiverId,
            NotificationKind.DiscountOfferReceived,
            "Primili ste token ponudu",
            $"{sender.FullName} nudi {dto.TokenAmount:0.##} tokena za \"{listing.Title}\".",
            offer.Id);

        // Ručno popuni navigacije za mapping (bez ponovnog DB upita)
        offer.Sender   = sender;
        offer.Receiver = receiver;
        offer.Listing  = listing;

        logger.LogInformation(
            "DiscountOffer #{Id} kreiran: {Sender} → {Receiver}, {Amount} tokena, listing {Listing}",
            offer.Id, senderId, dto.ReceiverId, dto.TokenAmount, dto.ListingId);

        return (MapOffer(offer), null);
    }

    /// <summary>
    /// Provider prihvata ponudu.
    /// Vrši transfer tokena od pošiljaoca ka primaocu i kreira 2 TokenTransaction zapisa.
    /// </summary>
    public async Task<(bool, string?)> AcceptOfferAsync(int offerId, string receiverId)
    {
        var offer = await db.DiscountTokenOffers
            .Include(o => o.Sender)
            .Include(o => o.Listing)
            .FirstOrDefaultAsync(o => o.Id == offerId && o.ReceiverId == receiverId);

        if (offer is null)
            return (false, "Ponuda nije pronađena.");

        if (offer.Status != DiscountOfferStatus.Pending)
            return (false, "Ponuda više nije aktivna.");

        var receiver = await db.Users.FindAsync(receiverId);
        if (receiver is null) return (false, "Korisnik nije pronađen.");

        var now = DateTime.UtcNow;

        // ── Transfer u transakciji ─────────────────────────────────────────
        // Isti obrazac kao u BoostService: provera i oduzimanje moraju biti
        // JEDNA SQL naredba. Ranije je ovde bilo pročitaj → uporedi → oduzmi,
        // pa su dva istovremena prihvatanja mogla oba proći i odvesti
        // pošiljaočev balans u minus.
        await using var trx = await db.Database.BeginTransactionAsync();

        var affected = await db.Users
            .Where(u => u.Id == offer.SenderId && u.TokenBalance >= offer.TokenAmount)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.TokenBalance,
                u => u.TokenBalance - offer.TokenAmount));

        if (affected == 0)
        {
            await trx.RollbackAsync();

            var trenutni = await db.Users
                .Where(u => u.Id == offer.SenderId)
                .Select(u => u.TokenBalance)
                .FirstOrDefaultAsync();

            return (false, $"Pošiljalac nema dovoljno tokena " +
                           $"(trenutni balans: {trenutni:0.##}).");
        }

        // ExecuteUpdateAsync zaobilazi change tracker — praćeni offer.Sender je
        // sada zastareo. Balans čitamo ponovo da bi BalanceAfter bio tačan.
        var balansPosiljaoca = await db.Users
            .Where(u => u.Id == offer.SenderId)
            .Select(u => u.TokenBalance)
            .FirstAsync();

        receiver.TokenBalance += offer.TokenAmount;

        offer.Status      = DiscountOfferStatus.Accepted;
        offer.RespondedAt = now;

        // Audit trail — sender
        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = offer.SenderId,
            Amount       = -offer.TokenAmount,
            Kind         = TokenKind.DiscountSent,
            Description  = $"Token popust poslan za \"{offer.Listing.Title}\"",
            ReferenceId  = offer.Id,
            BalanceAfter = balansPosiljaoca,
            CreatedAt    = now
        });

        // Audit trail — receiver
        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = receiver.Id,
            Amount       = offer.TokenAmount,
            Kind         = TokenKind.DiscountReceived,
            Description  = $"Primljeni token popust za \"{offer.Listing.Title}\"",
            ReferenceId  = offer.Id,
            BalanceAfter = receiver.TokenBalance,
            CreatedAt    = now
        });

        await db.SaveChangesAsync();

        // Transfer je konačan tek ovde — obe strane balansa, oba ledger zapisa
        // i status ponude idu zajedno ili nikako.
        await trx.CommitAsync();

        await notificationService.SendAsync(
            offer.SenderId,
            NotificationKind.DiscountOfferAccepted,
            "Ponuda prihvaćena!",
            $"Vaša ponuda od {offer.TokenAmount:0.##} tokena za \"{offer.Listing.Title}\" je prihvaćena.",
            offer.Id);

        logger.LogInformation(
            "DiscountOffer #{Id} prihvaćen — transfer {Amount} tokena od {From} ka {To}",
            offerId, offer.TokenAmount, offer.SenderId, receiverId);

        return (true, null);
    }

    /// <summary>Provider odbija ponudu. Nema tokens movementa — tokeni nisu bili rezervisani.</summary>
    public async Task<(bool, string?)> RejectOfferAsync(int offerId, string receiverId)
    {
        var offer = await db.DiscountTokenOffers
            .Include(o => o.Listing)
            .FirstOrDefaultAsync(o => o.Id == offerId && o.ReceiverId == receiverId);

        if (offer is null)
            return (false, "Ponuda nije pronađena.");

        if (offer.Status != DiscountOfferStatus.Pending)
            return (false, "Ponuda više nije aktivna.");

        var now = DateTime.UtcNow;
        offer.Status      = DiscountOfferStatus.Rejected;
        offer.RespondedAt = now;

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            offer.SenderId,
            NotificationKind.DiscountOfferRejected,
            "Ponuda odbijena",
            $"Vaša ponuda od {offer.TokenAmount:0.##} tokena za \"{offer.Listing.Title}\" je odbijena.",
            offer.Id);

        return (true, null);
    }

    /// <summary>Ponude koje je korisnik PRIMIO (kao provider/primalac).</summary>
    public async Task<List<DiscountOfferDto>> GetIncomingOffersAsync(string userId)
    {
        var offers = await db.DiscountTokenOffers
            .AsNoTracking()
            .Include(o => o.Sender)
            .Include(o => o.Receiver)
            .Include(o => o.Listing)
            .Where(o => o.ReceiverId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return offers.Select(MapOffer).ToList();
    }

    /// <summary>Ponude koje je korisnik POSLAO (kao klijent/pošiljalac).</summary>
    public async Task<List<DiscountOfferDto>> GetOutgoingOffersAsync(string userId)
    {
        var offers = await db.DiscountTokenOffers
            .AsNoTracking()
            .Include(o => o.Sender)
            .Include(o => o.Receiver)
            .Include(o => o.Listing)
            .Where(o => o.SenderId == userId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();

        return offers.Select(MapOffer).ToList();
    }

    // ── REFERRALS ──────────────────────────────────────────────────────────

    /// <summary>Sopstveni referral kod sa shareable linkom.</summary>
    public async Task<MyReferralCodeDto?> GetMyCodeAsync(string userId)
    {
        var code = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.ReferralCode)
            .FirstOrDefaultAsync();

        if (code is null) return null;

        var baseUrl = config["App:BaseUrl"] ?? "https://usluzionica.rs";
        return new MyReferralCodeDto
        {
            ReferralCode  = code,
            ShareableLink = $"{baseUrl}/register?ref={code}"
        };
    }

    /// <summary>
    /// Statistika referral programa — ukupno pozvano, koliko je postalo provajder, ukupna zarada.
    /// </summary>
    public async Task<ReferralStatsDto> GetReferralStatsAsync(string userId)
    {
        var referrals = await db.Referrals
            .AsNoTracking()
            .Include(r => r.ReferredUser)
            .Where(r => r.ReferrerId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return new ReferralStatsDto
        {
            TotalInvited        = referrals.Count,
            TotalBecameProvider = referrals.Count(r => r.Status == ReferralStatus.Rewarded),

            // "Pending" iz ugla korisnika = pozvan ali još nije provajder.
            // To su i Pending (nije potvrdio email) i Registered (jeste, ali
            // nije aktivirao provajdera) — obe su nedovršene.
            TotalPending        = referrals.Count(r => r.Status != ReferralStatus.Rewarded),

            // Zbir OBE rate. Da je ostalo samo na drugoj, korisnik bi u
            // statistici video manje tokena nego što mu je stvarno leglo.
            TotalTokensEarned   = referrals.Sum(r => r.TotalTokensAwarded),

            Referrals = referrals.Select(r => new ReferralEntryDto
            {
                InviteeName        = r.ReferredUser?.FullName ?? "Nepoznat",
                Status             = r.Status.ToString(),
                InvitedAt          = r.CreatedAt,
                SignupRewardedAt   = r.SignupRewardedAt,
                RewardedAt         = r.ActivationRewardedAt,
                SignupTokens       = r.SignupTokensAwarded,
                ActivationTokens   = r.ActivationTokensAwarded,
                TokensEarned       = r.TotalTokensAwarded
            }).ToList()
        };
    }

    // ── HELPER ─────────────────────────────────────────────────────────────

    private static DiscountOfferDto MapOffer(DiscountTokenOffer o) => new()
    {
        Id           = o.Id,
        SenderId     = o.SenderId,
        SenderName   = o.Sender?.FullName   ?? string.Empty,
        ReceiverId   = o.ReceiverId,
        ReceiverName = o.Receiver?.FullName ?? string.Empty,
        ListingId    = o.ListingId,
        ListingTitle = o.Listing?.Title     ?? string.Empty,
        TokenAmount  = o.TokenAmount,
        Status       = o.Status.ToString(),
        CreatedAt    = o.CreatedAt,
        RespondedAt  = o.RespondedAt
    };
}
