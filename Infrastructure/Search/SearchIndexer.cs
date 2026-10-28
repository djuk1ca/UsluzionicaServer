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
        listing.SearchLocation = Truncate(LocationIndex(listing), LocationMax);
        listing.SearchBody     = SearchNormalizer.Fold(listing.Description);  // varchar(max)
        listing.SearchVersion  = SearchNormalizer.Version;
    }

    /// <summary>Posao — isti oblik indeksa kao oglas, ista pretraga.</summary>
    public static void Apply(Job job)
    {
        job.SearchTitle    = Truncate(SearchNormalizer.Fold(job.Title),    TitleMax);
        job.SearchLocation = Truncate(SearchNormalizer.Fold(job.Location), LocationMax);
        job.SearchBody     = SearchNormalizer.Fold(job.Description);
        job.SearchVersion  = SearchNormalizer.Version;
    }

    public static void Apply(ApplicationUser user)
    {
        user.SearchName    = Truncate(SearchNormalizer.Fold(user.FullName), NameMax);
        user.SearchVersion = SearchNormalizer.Version;
    }

    /// <summary>Grad oglasa — foldovan za filter grada (prefiks po svakom gradu).</summary>
    public static void Apply(ListingCity city)
    {
        city.SearchCity = Truncate(SearchNormalizer.Fold(city.City), LocationMax);
    }

    /// <summary>
    /// SearchLocation oglasa: SVI njegovi gradovi, pa reči „online onlajn" ako
    /// je oglas online. Služi tekstualnom upitu — „moler cacak" nalazi oglas
    /// kome je Čačak drugi grad, a „casovi online" (ili „онлајн") online oglas,
    /// istim slojevima pretrage, bez posebnog predikata u svakom sloju.
    ///
    /// Filter grada NE ide preko ove kolone nego preko ListingCity.SearchCity:
    /// prefiks „beograd%" nad spojenim tekstom bi pogađao samo prvi grad.
    ///
    /// Redovi bez gradova u kolekciji (stari, pre tabele gradova) padaju
    /// nazad na Location, da se ne izgube iz pretrage.
    /// </summary>
    private static string LocationIndex(Listing listing)
    {
        var gradovi = listing.Cities.Count > 0
            ? listing.Cities.OrderBy(c => c.SortOrder).Select(c => c.City)
            : [listing.Location];

        var delovi = gradovi
            .Select(SearchNormalizer.Fold)
            .Where(g => g.Length > 0)
            .ToList();

        if (listing.IsOnline) delovi.Add(OnlineReci);

        return string.Join(' ', delovi);
    }

    private const string OnlineReci = "online onlajn";

    /// <summary>
    /// Preklapanje može PRODUŽITI tekst — „đ" postaje „dj", „љ" postaje „lj".
    /// Naslov od 300 znakova punih đ/ž postaje do ~600. Kolone su dimenzionisane
    /// sa rezervom, ali odsecanje je poslednja odbrana od DbUpdateException.
    /// </summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
