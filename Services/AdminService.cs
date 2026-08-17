using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Admin;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class AdminService(AppDbContext db, NotificationService notificationService)
{
    // ── KORISNICI ──────────────────────────────────────────────────────────

    public async Task<(List<AdminUserDto> Items, int Total)> GetUsersAsync(
        int page, int pageSize, string? search = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(page, 1);

        var query = db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.FullName.Contains(search) || u.Email!.Contains(search));

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserDto
            {
                Id           = u.Id,
                FullName     = u.FullName,
                Email        = u.Email ?? string.Empty,
                IsProvider   = u.IsProvider,
                IsPremium    = u.IsPremium,
                IsActive     = u.IsActive,
                TokenBalance = u.TokenBalance,
                CreatedAt    = u.CreatedAt
            })
            .ToListAsync();

        return (items, total);
    }

    public async Task<bool> DeactivateUserAsync(string userId)
    {
        var updated = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                u => u.IsActive,
                u => !u.IsActive));

        return updated > 0;
    }

    /// <summary>Admin ručno dodeljuje tokene korisniku (npr. kompenzacija, promocija).
    /// Upisuje se u ledger kao AdminGrant transakcija i šalje se notifikacija korisniku.</summary>
    public async Task<(bool Success, string? Error, decimal? NewBalance)> GrantTokensAsync(
        string userId, decimal amount, string? note)
    {
        if (amount <= 0)
            return (false, "Iznos mora biti veći od nule.", null);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
            return (false, "Korisnik nije pronađen.", null);

        var now = DateTime.UtcNow;
        var description = string.IsNullOrWhiteSpace(note)
            ? "Tokeni dodeljeni od strane administratora."
            : note.Trim();

        user.TokenBalance += amount;

        db.TokenTransactions.Add(new Domain.Entities.TokenTransaction
        {
            UserId       = user.Id,
            Amount       = amount,
            Kind         = TokenKind.AdminGrant,
            Description  = description,
            BalanceAfter = user.TokenBalance,
            CreatedAt    = now
        });

        await db.SaveChangesAsync();

        await notificationService.SendAsync(
            user.Id, NotificationKind.TokenEarned,
            "Dodeljeni tokeni",
            $"Administrator ti je dodelio {amount:0.##} tokena.");

        return (true, null, user.TokenBalance);
    }

    // ── LISTINZI ───────────────────────────────────────────────────────────

    public async Task<(List<AdminListingDto> Items, int Total)> GetListingsAsync(
        int page, int pageSize, string? status = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(page, 1);

        var query = db.Listings
            .AsNoTracking()
            .Include(l => l.Category)
            .Include(l => l.ProviderProfile).ThenInclude(pp => pp.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<ListingStatus>(status, ignoreCase: true, out var parsed))
        {
            query = query.Where(l => l.Status == parsed);
        }

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(l => l.BoostScore)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new AdminListingDto
            {
                Id           = l.Id,
                Title        = l.Title,
                CategoryName = l.Category.Name,
                ProviderName = l.ProviderProfile.User.FullName,
                Status       = l.Status.ToString(),
                IsBoosted    = l.IsBoosted,
                ViewCount    = l.ViewCount,
                CreatedAt    = l.CreatedAt,
                BoostScore = l.BoostScore
            })
            .ToListAsync();

        return (items, total);
    }

    public async Task<bool> ArchiveListingAsync(int listingId)
    {
        var updated = await db.Listings
            .Where(l => l.Id == listingId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, ListingStatus.Archived));

        return updated > 0;
    }

    // ── PROVAJDERI ─────────────────────────────────────────────────────────

    public async Task<bool> VerifyProviderAsync(int providerProfileId)
    {
        var updated = await db.ProviderProfiles
            .Where(p => p.Id == providerProfileId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsVerified, true));

        return updated > 0;
    }

    // ── TOKEN LOG ──────────────────────────────────────────────────────────

    public async Task<(List<AdminTokenLogDto> Items, int Total)> GetTokenLogAsync(
        int page, int pageSize, string? kind = null, string? userId = null)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        page     = Math.Max(page, 1);

        var query = db.TokenTransactions
            .AsNoTracking()
            .Include(t => t.User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(userId))
            query = query.Where(t => t.UserId == userId);

        if (!string.IsNullOrWhiteSpace(kind) &&
            Enum.TryParse<TokenKind>(kind, ignoreCase: true, out var parsedKind))
        {
            query = query.Where(t => t.Kind == parsedKind);
        }

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new AdminTokenLogDto
            {
                Id           = t.Id,
                UserId       = t.UserId,
                UserName     = t.User.FullName,
                Amount       = t.Amount,
                Kind         = t.Kind.ToString(),
                Description  = t.Description,
                BalanceAfter = t.BalanceAfter,
                CreatedAt    = t.CreatedAt
            })
            .ToListAsync();

        return (items, total);
    }

    // ── STATISTIKE ─────────────────────────────────────────────────────────

    public async Task<AdminStatsDto> GetStatsAsync()
    {
        // EF Core ne podržava paralelne operacije na istom DbContext-u — sekvencijalni await
        return new AdminStatsDto
        {
            TotalUsers               = await db.Users.CountAsync(),
            ActiveUsers              = await db.Users.CountAsync(u => u.IsActive),
            TotalProviders           = await db.ProviderProfiles.CountAsync(),
            VerifiedProviders        = await db.ProviderProfiles.CountAsync(p => p.IsVerified),
            TotalListings            = await db.Listings.CountAsync(),
            ActiveListings           = await db.Listings.CountAsync(l => l.Status == ListingStatus.Active),
            TotalTokensInCirculation = await db.Users.SumAsync(u => (decimal?)u.TokenBalance) ?? 0m,
            TotalTokensPurchased     = await db.TokenPurchases
                                           .Where(p => p.Status == TokenPurchaseStatus.Completed)
                                           .SumAsync(p => (decimal?)p.Tokens) ?? 0m,
            TotalRevenueRsd          = await db.TokenPurchases
                                           .Where(p => p.Status == TokenPurchaseStatus.Completed)
                                           .SumAsync(p => (decimal?)p.AmountRsd) ?? 0m,
            TotalBookings            = await db.BookingRequests.CountAsync(),
            CompletedBookings        = await db.BookingRequests.CountAsync(b => b.Status == BookingStatus.Completed),
            PendingBookings          = await db.BookingRequests.CountAsync(b => b.Status == BookingStatus.Pending),
        };
    }

    // ── ANALITIKA (grafikoni) ──────────────────────────────────────────────

    public async Task<TokenAnalyticsDto> GetTokenAnalyticsAsync(int days = 30)
    {
        days = Math.Clamp(days, 7, 365);
        var since = DateTime.UtcNow.AddDays(-days).Date;

        // ServiceReward — tokeni ka klijentima za izvršene usluge
        var serviceRewards = await db.TokenTransactions
            .AsNoTracking()
            .Where(t => t.Kind == TokenKind.ServiceReward && t.CreatedAt >= since)
            .GroupBy(t => DateOnly.FromDateTime(t.CreatedAt))
            .Select(g => new DailyTokenStat
            {
                Date        = g.Key,
                TotalTokens = g.Sum(t => t.Amount),
                Count       = g.Count()
            })
            .OrderBy(s => s.Date)
            .ToListAsync();

        // DiscountReceived — token ponude primljene od klijenata
        var discountTransfers = await db.TokenTransactions
            .AsNoTracking()
            .Where(t => t.Kind == TokenKind.DiscountReceived && t.CreatedAt >= since)
            .GroupBy(t => DateOnly.FromDateTime(t.CreatedAt))
            .Select(g => new DailyTokenStat
            {
                Date        = g.Key,
                TotalTokens = g.Sum(t => t.Amount),
                Count       = g.Count()
            })
            .OrderBy(s => s.Date)
            .ToListAsync();

        // BoostSpend — tokeni potrošeni za boost (Amount je negativan → uzimamo apsolutnu vrednost)
        var boostSpends = await db.TokenTransactions
            .AsNoTracking()
            .Where(t => t.Kind == TokenKind.BoostSpend && t.CreatedAt >= since)
            .GroupBy(t => DateOnly.FromDateTime(t.CreatedAt))
            .Select(g => new DailyTokenStat
            {
                Date        = g.Key,
                TotalTokens = -g.Sum(t => t.Amount), // ABS: Amount je negativan
                Count       = g.Count()
            })
            .OrderBy(s => s.Date)
            .ToListAsync();

        // TokenPurchase — kupovine tokena (posebna tabela, amount u RSD)
        var purchases = await db.TokenPurchases
            .AsNoTracking()
            .Where(p => p.Status == TokenPurchaseStatus.Completed && p.CreatedAt >= since)
            .GroupBy(p => DateOnly.FromDateTime(p.CreatedAt))
            .Select(g => new DailyTokenStat
            {
                Date        = g.Key,
                TotalTokens = g.Sum(p => p.Tokens),
                Count       = g.Count()
            })
            .OrderBy(s => s.Date)
            .ToListAsync();

        // TotalByKind — ukupno po vrsti za pie chart
        var totalByKind = await db.TokenTransactions
            .AsNoTracking()
            .Where(t => t.CreatedAt >= since)
            .GroupBy(t => t.Kind)
            .Select(g => new { Kind = g.Key.ToString(), Total = g.Sum(t => Math.Abs(t.Amount)) })
            .ToListAsync();

        return new TokenAnalyticsDto
        {
            ServiceRewards    = serviceRewards,
            DiscountTransfers = discountTransfers,
            BoostSpends       = boostSpends,
            Purchases         = purchases,
            TotalByKind       = totalByKind.ToDictionary(x => x.Kind, x => x.Total)
        };
    }
}
