using Microsoft.AspNetCore.Identity;

namespace UsluzionicaServer.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    public string          FullName        { get; set; } = string.Empty;
    public decimal         TokenBalance    { get; set; } = 0m;
    public string?         ProfileImageUrl { get; set; }
    public string?         LastKnownCity   { get; set; }
    public bool            IsProvider      { get; set; } = false;
    public bool            IsPremium       { get; set; } = false;
    public DateTime        CreatedAt       { get; set; } = DateTime.UtcNow;
    public bool            IsActive        { get; set; } = true;
    public string?         ReferralCode    { get; set; }   // 8-char unique, generisan pri registraciji

    // ── Denormalizovani indeks za pretragu ─────────────────────────────────
    // Održava AppDbContext.SaveChanges kroz SearchIndexer. Koristi ga admin
    // pretraga korisnika, da "milos" nađe i "Miloš".
    public string SearchName    { get; set; } = string.Empty;
    public int    SearchVersion { get; set; }

    // Navigation
    public ProviderProfile?             ProviderProfile      { get; set; }
    public ICollection<Referral>        ReferralsSent        { get; set; } = [];  // korisnici koje je ovaj pozvao
    public ICollection<RefreshToken>    RefreshTokens        { get; set; } = [];
    public ICollection<Conversation>    ConversationsAsUser1 { get; set; } = [];
    public ICollection<Conversation>    ConversationsAsUser2 { get; set; } = [];
    public ICollection<Message>         SentMessages         { get; set; } = [];
    public ICollection<BookingRequest>  BookingsAsClient     { get; set; } = [];
    public ICollection<BookingRequest>  BookingsAsProvider   { get; set; } = [];
    public ICollection<Review>          Reviews              { get; set; } = [];
    public ICollection<TokenTransaction> TokenTransactions   { get; set; } = [];
    public ICollection<TokenPurchase>   TokenPurchases       { get; set; } = [];
    public ICollection<ListingBoost>    ListingBoosts        { get; set; } = [];
    public ICollection<Notification>     Notifications        { get; set; } = [];
    public ICollection<FavoriteListing>  FavoriteListings     { get; set; } = [];
    public ICollection<FavoriteProvider> FavoriteProviders    { get; set; } = [];
}
