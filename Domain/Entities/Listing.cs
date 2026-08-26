using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class Listing
{
    public int           Id                { get; set; }
    public int           ProviderProfileId { get; set; }
    public int           CategoryId        { get; set; }
    public string        Title             { get; set; } = string.Empty;
    public string        Description       { get; set; } = string.Empty;
    public string        Location          { get; set; } = string.Empty;
    public PriceMode     PriceMode         { get; set; }
    public decimal?      FixedPrice        { get; set; }
    public decimal?      PriceFrom         { get; set; }
    public decimal?      PriceTo           { get; set; }
    public ListingStatus Status            { get; set; } = ListingStatus.Active;
    public int           ViewCount         { get; set; } = 0;
    public bool          IsBoosted         { get; set; } = false;
    public DateTime?     BoostExpiresAt    { get; set; }
    public decimal       BoostScore        { get; set; } = 0m;
    public DateTime      CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime      UpdatedAt         { get; set; } = DateTime.UtcNow;

    // ── Denormalizovani indeks za pretragu ─────────────────────────────────
    // Foldovane kopije Title/Location/Description (mala slova, bez dijakritike,
    // ćirilica preslovljena). Održava ih AppDbContext.SaveChanges kroz
    // SearchIndexer — NIKAD se ne postavljaju ručno u servisima.
    //
    // Postoje da bi upit i sadržaj završili u istom obliku, pa da "sisanje"
    // nađe "Šišanje" bez zavisnosti od collation-a baze.

    /// <summary>Fold(Title). Visok signal — indeksiran.</summary>
    public string SearchTitle    { get; set; } = string.Empty;

    /// <summary>Fold(Location). Odvojen da filter grada bude indeksirani seek.</summary>
    public string SearchLocation { get; set; } = string.Empty;

    /// <summary>Fold(Description). Nizak signal — pretražuje se samo kad je gornji sloj tanak.</summary>
    public string SearchBody     { get; set; } = string.Empty;

    /// <summary>Verzija pravila preklapanja kojom je red indeksiran. Vidi SearchIndexBackfill.</summary>
    public int    SearchVersion  { get; set; }

    // Navigation
    public ProviderProfile               ProviderProfile       { get; set; } = null!;
    public Category                      Category              { get; set; } = null!;
    public ICollection<ListingImage>     Images                { get; set; } = [];
    public ICollection<BookingRequest>   BookingRequests       { get; set; } = [];
    public ICollection<Review>           Reviews               { get; set; } = [];
    public ICollection<ListingBoost>     Boosts                { get; set; } = [];
    public ICollection<DiscountTokenOffer> DiscountOffers      { get; set; } = [];
}
