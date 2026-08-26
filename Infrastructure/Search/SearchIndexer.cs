using UsluzionicaServer.Domain.Entities;

namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Popunjava denormalizovane Search* kolone.
///
/// Poziva se iz <c>AppDbContext.SaveChanges</c>, a NE iz servisa. Razlog:
/// da se održavanje indeksa radi u <c>ListingService.CreateAsync</c>/<c>UpdateAsync</c>,
/// svaki budući put pisanja (admin izmena, seed, ručna popravka podataka) bio
/// bi jedna zaboravljena linija od tihog raspada pretrage — oglas bi postojao
/// u bazi ali se ne bi mogao naći.
///
/// Ovako je nemoguće zaobići: sve što prođe kroz change tracker dobija svež
/// indeks.
/// </summary>
public static class SearchIndexer
{
    // Dužine prate ograničenja kolona iz AppDbContext konfiguracije.
    private const int TitleMax    = 700;
    private const int LocationMax = 420;
    private const int NameMax     = 400;

    public static void Apply(Listing listing)
    {
        listing.SearchTitle    = Truncate(SearchNormalizer.Fold(listing.Title),    TitleMax);
        listing.SearchLocation = Truncate(SearchNormalizer.Fold(listing.Location), LocationMax);
        listing.SearchBody     = SearchNormalizer.Fold(listing.Description);  // varchar(max)
        listing.SearchVersion  = SearchNormalizer.Version;
    }

    public static void Apply(ApplicationUser user)
    {
        user.SearchName    = Truncate(SearchNormalizer.Fold(user.FullName), NameMax);
        user.SearchVersion = SearchNormalizer.Version;
    }

    /// <summary>
    /// Preklapanje može PRODUŽITI tekst — „đ" postaje „dj", „љ" postaje „lj".
    /// Naslov od 300 znakova punih đ/ž postaje do ~600. Kolone su dimenzionisane
    /// sa rezervom, ali odsecanje je poslednja odbrana od DbUpdateException.
    /// </summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
