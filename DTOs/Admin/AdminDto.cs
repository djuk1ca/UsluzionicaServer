namespace UsluzionicaServer.DTOs.Admin;

public sealed class AdminUserDto
{
    public string   Id           { get; init; } = string.Empty;
    public string   FullName     { get; init; } = string.Empty;
    public string   Email        { get; init; } = string.Empty;
    public bool     IsProvider   { get; init; }
    public bool     IsPremium    { get; init; }
    public bool     IsActive     { get; init; }
    public decimal  TokenBalance { get; init; }
    public DateTime CreatedAt    { get; init; }
}

public sealed class AdminListingDto
{
    public int      Id           { get; init; }
    public string   Title        { get; init; } = string.Empty;
    public string   CategoryName { get; init; } = string.Empty;
    public string   ProviderName { get; init; } = string.Empty;
    public string   Status       { get; init; } = string.Empty;
    public bool     IsBoosted    { get; init; }
    public int      ViewCount    { get; init; }
    public DateTime CreatedAt    { get; init; }
    public decimal BoostScore {  get; init; }   
}

public sealed class AdminTokenLogDto
{
    public int      Id           { get; init; }
    public string   UserId       { get; init; } = string.Empty;
    public string   UserName     { get; init; } = string.Empty;
    public decimal  Amount       { get; init; }
    public string   Kind         { get; init; } = string.Empty;
    public string   Description  { get; init; } = string.Empty;
    public decimal  BalanceAfter { get; init; }
    public DateTime CreatedAt    { get; init; }
}

public sealed class AdminStatsDto
{
    public int     TotalUsers              { get; init; }
    public int     ActiveUsers             { get; init; }
    public int     TotalProviders          { get; init; }
    public int     VerifiedProviders       { get; init; }
    public int     TotalListings           { get; init; }
    public int     ActiveListings          { get; init; }
    public decimal TotalTokensInCirculation { get; init; }
    public decimal TotalTokensPurchased    { get; init; }
    public decimal TotalRevenueRsd         { get; init; }
    public int     TotalBookings           { get; init; }
    public int     CompletedBookings       { get; init; }
    public int     PendingBookings         { get; init; }
}

public sealed class DailyTokenStat
{
    public DateOnly Date        { get; init; }
    public decimal  TotalTokens { get; init; }
    public int      Count       { get; init; }
}

public sealed class TokenAnalyticsDto
{
    public List<DailyTokenStat>        ServiceRewards    { get; init; } = [];
    public List<DailyTokenStat>        DiscountTransfers { get; init; } = [];
    public List<DailyTokenStat>        BoostSpends       { get; init; } = [];
    public List<DailyTokenStat>        Purchases         { get; init; } = [];
    public Dictionary<string, decimal> TotalByKind       { get; init; } = [];
}

public sealed class GrantTokensDto
{
    [System.ComponentModel.DataAnnotations.Range(0.01, 1_000_000)]
    public decimal Amount { get; init; }
    [System.ComponentModel.DataAnnotations.MaxLength(300)]
    public string? Note { get; init; }
}
