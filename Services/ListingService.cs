using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.Infrastructure.Search;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

public sealed class ListingService(
    AppDbContext                 db,
    UserManager<ApplicationUser> userManager,
    IWebHostEnvironment          env,
    CategorySearchIndex          categoryIndex,
    ILogger<ListingService>      logger)
{
    // ── Podešavanja slojevite pretrage ─────────────────────────────────────

    /// <summary>Ispod ovoga se prelazi na sledeći, širi sloj pretrage.</summary>
    private const int MinAcceptableResults = 3;

    /// <summary>Gornja granica kandidata za OR sloj (skorovanje u memoriji).</summary>
    private const int PartialCandidateCap = 600;

    /// <summary>Gornja granica kandidata za fuzzy sloj.</summary>
    private const int FuzzyCandidateCap = 500;

    /// <summary>Escape znak za LIKE — vidi SearchNormalizer.EscapeLike.</summary>
    private const string LikeEscape = "\\";

    // ── SEARCH (javno) ─────────────────────────────────────────────────────
    /// <summary>
    /// Pretražuje aktivne oglase, tolerantno na dijakritiku, pismo i tipfelere.
    ///
    /// Radi u slojevima, od najpreciznijeg ka najširem. Prelazi se na sledeći
    /// sloj samo ako prethodni vrati premalo rezultata — tako uobičajen upit
    /// nikad ne plati cenu fuzzy pretrage:
    ///
    ///   1a. Svi tokeni u naslovu / lokaciji / kategoriji  → čist SQL, ~90% upita
    ///   1b. Isto + opis                                   → skuplja, varchar(max)
    ///   2.  Bar jedan token (OR), skorovano u memoriji
    ///   3.  Fuzzy — pigeonhole prefilter u SQL-u + OSA rastojanje
    ///
    /// Sortiranje unutar 1a/1b ostaje kao pre: boosted → BoostScore → datum.
    /// U slojevima 2 i 3 skor pretrage je primaran, pa boost kao razrešivač.
    /// </summary>
    public async Task<PagedResult<ListingDto>> SearchAsync(ListingQueryParams p)
    {
        p.PageSize = Math.Clamp(p.PageSize, 1, 50);
        p.Page     = Math.Max(p.Page, 1);

        var baseQuery = await BuildBaseQueryAsync(p);
        var query     = SearchQuery.Parse(p.Q);

        // Bez tekstualnog upita nema šta da se skoruje — samo filteri i sort.
        if (query.IsEmpty)
            return await PageFromSqlAsync(baseQuery, p);

        var categoryMatches = await categoryIndex.MatchPerTokenAsync(query);

        // ── Sloj 1a ────────────────────────────────────────────────────────
        var tier1a  = ApplyAllTokens(baseQuery, query, categoryMatches, includeBody: false);
        var count1a = await tier1a.CountAsync();

        if (count1a >= MinAcceptableResults)
            return await PageFromSqlAsync(tier1a, p, count1a);

        // ── Sloj 1b ────────────────────────────────────────────────────────
        // Opis je varchar(max) — LIKE '%x%' nad njim je jedina zaista skupa
        // operacija u ovom dizajnu, pa se radi tek kad uži sloj ne da dovoljno.
        var tier1b  = ApplyAllTokens(baseQuery, query, categoryMatches, includeBody: true);
        var count1b = await tier1b.CountAsync();

        if (count1b >= MinAcceptableResults)
            return await PageFromSqlAsync(tier1b, p, count1b);

        // ── Slojevi 2 i 3 ──────────────────────────────────────────────────
        // Rezultati iz 1b se ZADRŽAVAJU i spajaju sa širim kandidatima — inače
        // bismo odbacili ono malo tačnih pogodaka koje smo već našli.
        return await ScoredSearchAsync(baseQuery, tier1b, query, categoryMatches, p);
    }

    // ── Osnovni filteri (status, kategorija, grad) ─────────────────────────
    private async Task<IQueryable<Listing>> BuildBaseQueryAsync(ListingQueryParams p)
    {
        var query = db.Listings
            .AsNoTracking()
            .Include(l => l.Category)
            .Include(l => l.Images)
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .Where(l => l.Status == ListingStatus.Active);

        // Filter po kategoriji (slug) — roditelj povlači i podkategorije.
        if (!string.IsNullOrWhiteSpace(p.CategorySlug))
        {
            var slug = p.CategorySlug.Trim().ToLowerInvariant();
            var categoryIds = await db.Categories
                .Where(c => c.Slug == slug || (c.Parent != null && c.Parent.Slug == slug))
                .Select(c => c.Id)
                .ToListAsync();

            if (categoryIds.Count > 0)
                query = query.Where(l => categoryIds.Contains(l.CategoryId));
        }

        // Filter po gradu — sada nad foldovanom kolonom.
        // Ranije je bilo `l.Location == city`, tačna jednakost: „cacak" nije
        // nalazilo „Čačak", a „beograd" nije nalazilo „Beograd — Vračar".
        // Sada je poređenje po foldovanoj vrednosti, uz LIKE prefiks da bi
        // beogradske opštine ostale obuhvaćene gradom „Beograd".
        if (!string.IsNullOrWhiteSpace(p.City))
        {
            var city = SearchNormalizer.Fold(p.City);
            if (city.Length > 0)
            {
                var pattern = SearchNormalizer.EscapeLike(city) + "%";
                query = query.Where(l => EF.Functions.Like(l.SearchLocation, pattern, LikeEscape));
            }
        }

        return query;
    }

    // ── Sloj 1: SVI tokeni moraju biti pogođeni (AND) ──────────────────────
    private static IQueryable<Listing> ApplyAllTokens(
        IQueryable<Listing>                          source,
        SearchQuery                                  query,
        IReadOnlyDictionary<string, HashSet<int>>    categoryMatches,
        bool                                         includeBody)
    {
        foreach (var token in query.Tokens)
        {
            // Unutar tokena je OR (varijante zbog „đ" + polja), između tokena AND.
            var predicates = new List<Expression<Func<Listing, bool>>>();

            foreach (var variant in token.Variants)
            {
                var pattern = "%" + SearchNormalizer.EscapeLike(variant) + "%";

                predicates.Add(l => EF.Functions.Like(l.SearchTitle,    pattern, LikeEscape));
                predicates.Add(l => EF.Functions.Like(l.SearchLocation, pattern, LikeEscape));

                // Ime uslugodavca — korisnik često pamti majstora po imenu, ne
                // po naslovu oglasa. Čita se UŽIVO kroz join (kolona je
                // indeksirana), a ne denormalizovano u oglas: kad korisnik
                // promeni ime, svi njegovi oglasi bi inače ostali sa starim.
                predicates.Add(l => EF.Functions.Like(
                    l.ProviderProfile.User.SearchName, pattern, LikeEscape));

                if (includeBody)
                    predicates.Add(l => EF.Functions.Like(l.SearchBody, pattern, LikeEscape));
            }

            // Kategorija se poredi PO TOKENU — „frizer beograd" ne sme vratiti
            // sve iz kategorije „Frizerski saloni" bez obzira na grad.
            if (categoryMatches.TryGetValue(token.Value, out var categoryIds))
            {
                var ids = categoryIds.ToList();
                predicates.Add(l => ids.Contains(l.CategoryId));
            }

            var combined = PredicateBuilder.OrAll(predicates);
            if (combined is not null)
                source = source.Where(combined);
        }

        return source;
    }

    // ── Slojevi 2 i 3: skorovanje u memoriji ───────────────────────────────
    private async Task<PagedResult<ListingDto>> ScoredSearchAsync(
        IQueryable<Listing>                       baseQuery,
        IQueryable<Listing>                       tier1b,
        SearchQuery                               query,
        IReadOnlyDictionary<string, HashSet<int>> categoryMatches,
        ListingQueryParams                        p)
    {
        // Kandidati se skupljaju iz tri izvora i dedupliraju po Id-u.
        var candidates = new Dictionary<int, Listing>();

        // (a) ono malo tačnih pogodaka iz sloja 1b
        foreach (var listing in await tier1b.Take(MinAcceptableResults * 4).ToListAsync())
            candidates[listing.Id] = listing;

        // (b) sloj 2 — bar jedan token u naslovu/lokaciji/kategoriji.
        //     NAMERNO bez opisa: OR nad tokenima opisa je upravo mehanizam
        //     koji proizvodi smeće (svaki oglas pominje „usluga", „kvalitet"…).
        var orPredicates = new List<Expression<Func<Listing, bool>>>();
        foreach (var token in query.Tokens)
        {
            foreach (var variant in token.Variants)
            {
                var pattern = "%" + SearchNormalizer.EscapeLike(variant) + "%";
                orPredicates.Add(l => EF.Functions.Like(l.SearchTitle,    pattern, LikeEscape));
                orPredicates.Add(l => EF.Functions.Like(l.SearchLocation, pattern, LikeEscape));
                orPredicates.Add(l => EF.Functions.Like(
                    l.ProviderProfile.User.SearchName, pattern, LikeEscape));
            }

            if (categoryMatches.TryGetValue(token.Value, out var categoryIds))
            {
                var ids = categoryIds.ToList();
                orPredicates.Add(l => ids.Contains(l.CategoryId));
            }
        }

        var orCombined = PredicateBuilder.OrAll(orPredicates);
        if (orCombined is not null)
        {
            var partial = await baseQuery
                .Where(orCombined)
                .OrderByDescending(l => l.IsBoosted)
                .ThenByDescending(l => l.BoostScore)
                .ThenByDescending(l => l.CreatedAt)
                .Take(PartialCandidateCap)
                .ToListAsync();

            foreach (var listing in partial)
                candidates[listing.Id] = listing;
        }

        // (c) sloj 3 — fuzzy prefilter po pigeonhole principu.
        //     Ako se upit i naslov razlikuju za najviše d izmena, a upit je
        //     podeljen na d+1 disjunktnih delova, bar jedan deo mora doslovno
        //     postojati u naslovu. Zato ovaj prefilter NE MOŽE promašiti pravi
        //     rezultat — korektan je po konstrukciji, nije heuristika.
        var fuzzyPredicates = new List<Expression<Func<Listing, bool>>>();
        foreach (var token in query.Tokens)
        {
            if (token.MaxDistance == 0) continue;   // prekratak token, bez tolerancije

            foreach (var fragment in token.Fragments)
            {
                var pattern = "%" + SearchNormalizer.EscapeLike(fragment) + "%";
                fuzzyPredicates.Add(l => EF.Functions.Like(l.SearchTitle, pattern, LikeEscape));
            }
        }

        var fuzzyCombined = PredicateBuilder.OrAll(fuzzyPredicates);
        if (fuzzyCombined is not null)
        {
            var fuzzy = await baseQuery
                .Where(fuzzyCombined)
                .OrderByDescending(l => l.IsBoosted)
                .ThenByDescending(l => l.BoostScore)
                .ThenByDescending(l => l.CreatedAt)
                .Take(FuzzyCandidateCap)
                .ToListAsync();

            foreach (var listing in fuzzy)
                candidates[listing.Id] = listing;
        }

        // ── Skorovanje i odsecanje ─────────────────────────────────────────
        var scored = candidates.Values
            .Select(l => new { Listing = l, Score = ScoreListing(l, query, categoryMatches) })
            .Where(x => x.Score >= Fuzzy.MinScore)   // ispod praga je smeće
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Listing.IsBoosted)
            .ThenByDescending(x => x.Listing.BoostScore)
            .ThenByDescending(x => x.Listing.CreatedAt)
            .ToList();

        var page = scored
            .Skip((p.Page - 1) * p.PageSize)
            .Take(p.PageSize)
            .Select(x => MapToDto(x.Listing))
            .ToList();

        logger.LogDebug(
            "Pretraga '{Query}': {Candidates} kandidata, {Scored} iznad praga (fuzzy sloj)",
            query.Folded, candidates.Count, scored.Count);

        return new PagedResult<ListingDto>
        {
            Items    = page,
            Total    = scored.Count,
            Page     = p.Page,
            PageSize = p.PageSize
        };
    }

    /// <summary>
    /// Koliko oglas odgovara upitu: 0.0–1.0.
    ///
    /// SVAKI token mora biti pogođen. Kad korisnik doda reč, on SUŽAVA
    /// pretragu — sloj skorovanja postoji zbog tolerancije na tipfelere, ne da
    /// bi ispuštao tokene.
    ///
    /// Bez tog pravila prosek zavarava: upit „frizerski novi" nad oglasom
    /// „Frizerski salon" u Subotici daje (1.0 + 0) / 2 = 0.5, što je iznad
    /// praga — pa bi korisnik koji traži frizera u Novom Sadu dobio frizere iz
    /// cele Srbije. (Ovo je uhvatio test, nije bilo očigledno iz koda.)
    ///
    /// Vraća 0 ako ijedan token nije pogođen; inače prosek, koji služi za
    /// rangiranje između oglasa koji su svi zadovoljili uslov.
    /// </summary>
    private static double ScoreListing(
        Listing                                   listing,
        SearchQuery                               query,
        IReadOnlyDictionary<string, HashSet<int>> categoryMatches)
    {
        var titleWords    = listing.SearchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var locationWords = listing.SearchLocation.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Ime uslugodavca — navigacija je uključena u upit, ali defanzivno
        // proveravamo jer se ScoreListing može pozvati i nad drugačije
        // učitanim entitetom.
        var providerWords = listing.ProviderProfile?.User?.SearchName is { Length: > 0 } name
            ? name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        double sum = 0;

        foreach (var token in query.Tokens)
        {
            double best = 0;

            foreach (var variant in token.Variants)
            {
                foreach (var word in titleWords)
                    best = Math.Max(best, Fuzzy.Similarity(variant, word));

                // Lokacija nosi nešto manju težinu od naslova — korisnik
                // prvenstveno traži uslugu, grad je dopunski signal.
                foreach (var word in locationWords)
                    best = Math.Max(best, Fuzzy.Similarity(variant, word) * 0.8);

                // Ime uslugodavca je jak signal, ali malo ispod naslova — kad
                // se traži „milan", oglas sa „Milan" u naslovu treba da bude
                // ispred oglasa čiji se majstor tako zove.
                foreach (var word in providerWords)
                    best = Math.Max(best, Fuzzy.Similarity(variant, word) * 0.9);
            }

            // Pogodak kroz kategoriju je jak, ali ne jači od pogotka u naslovu.
            if (categoryMatches.TryGetValue(token.Value, out var ids) && ids.Contains(listing.CategoryId))
                best = Math.Max(best, 0.85);

            // Prag je isti kao globalni: tipfeler-poklapanje ga prelazi
            // (~0.8), a potpuni promašaj je 0.
            if (best < Fuzzy.MinScore) return 0;

            sum += best;
        }

        return sum / query.Tokens.Count;
    }

    // ── Paginacija nad SQL upitom (slojevi 1a/1b i prazan upit) ────────────
    private async Task<PagedResult<ListingDto>> PageFromSqlAsync(
        IQueryable<Listing> query, ListingQueryParams p, int? knownTotal = null)
    {
        var total = knownTotal ?? await query.CountAsync();

        var items = await query
            .OrderByDescending(l => l.IsBoosted)
            .ThenByDescending(l => l.BoostScore)
            .ThenByDescending(l => l.CreatedAt)
            .Skip((p.Page - 1) * p.PageSize)
            .Take(p.PageSize)
            .ToListAsync();

        return new PagedResult<ListingDto>
        {
            Items    = items.Select(MapToDto).ToList(),
            Total    = total,
            Page     = p.Page,
            PageSize = p.PageSize
        };
    }

    // ── GET BY ID (javno, uvećava ViewCount) ───────────────────────────────
    /// <summary>
    /// Vraća detalje listinga. ViewCount se uvećava samo ako gledalac NIJE vlasnik
    /// (anoniman ili drugi korisnik = pravi pregled).
    /// </summary>
    public async Task<ListingDto?> GetByIdAsync(int id, string? viewerUserId = null)
    {
        var listing = await db.Listings
            .Include(l => l.Category)
            .Include(l => l.Images.OrderBy(i => i.SortOrder))
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .FirstOrDefaultAsync(l => l.Id == id && l.Status != ListingStatus.Archived);

        if (listing is null) return null;

        // Ne broj svoje preglede (vlasnik koji otvara svoj oglas)
        if (viewerUserId != listing.ProviderProfile.UserId)
        {
            await db.Listings
                .Where(l => l.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ViewCount, l => l.ViewCount + 1));
        }

        return MapToDto(listing);
    }

    // ── CREATE ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Kreira listing. Korisnik mora imati ProviderProfile.
    /// Validira PriceMode logiku.
    /// </summary>
    public async Task<(ListingDto? Result, string? Error)> CreateAsync(
        string userId, CreateListingDto dto)
    {
        // Proveravamo da korisnik ima provajder profil
        var provider = await db.ProviderProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);

        if (provider is null)
            return (null, "Moraš imati kreiran Provider profil da bi objavljivao listinge.");

        // Validacija kategorije
        var category = await db.Categories.FindAsync(dto.CategoryId);
        if (category is null)
            return (null, $"Kategorija sa Id={dto.CategoryId} ne postoji.");

        // Validacija cene
        var priceError = ValidatePrice(dto.PriceMode, dto.FixedPrice, dto.PriceFrom, dto.PriceTo);
        if (priceError is not null) return (null, priceError);

        var listing = new Listing
        {
            ProviderProfileId = provider.Id,
            CategoryId        = dto.CategoryId,
            Title             = dto.Title.Trim(),
            Description       = dto.Description.Trim(),
            Location          = dto.Location.Trim(),
            PriceMode         = dto.PriceMode,
            FixedPrice        = dto.FixedPrice,
            PriceFrom         = dto.PriceFrom,
            PriceTo           = dto.PriceTo,
            Status            = ListingStatus.Active,
            CreatedAt         = DateTime.UtcNow,
            UpdatedAt         = DateTime.UtcNow
        };

        db.Listings.Add(listing);

        // Uvećajmo TotalListings na provajder profilu
        provider.TotalListings++;

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Listing kreiran: {Id} od provajdera {ProviderId}", listing.Id, provider.Id);

        // Učitaj navigation properties za DTO
        listing.Category        = category;
        listing.ProviderProfile = provider;

        var user = await userManager.FindByIdAsync(userId);
        listing.ProviderProfile.User = user!;

        return (MapToDto(listing), null);
    }

    // ── UPDATE ─────────────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateAsync(
        int listingId, string userId, UpdateListingDto dto)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo izmene.");

        var category = await db.Categories.FindAsync(dto.CategoryId);
        if (category is null)
            return (false, $"Kategorija sa Id={dto.CategoryId} ne postoji.");

        var priceError = ValidatePrice(dto.PriceMode, dto.FixedPrice, dto.PriceFrom, dto.PriceTo);
        if (priceError is not null) return (false, priceError);

        listing.Title       = dto.Title.Trim();
        listing.Description = dto.Description.Trim();
        listing.Location    = dto.Location.Trim();
        listing.CategoryId  = dto.CategoryId;
        listing.PriceMode   = dto.PriceMode;
        listing.FixedPrice  = dto.FixedPrice;
        listing.PriceFrom   = dto.PriceFrom;
        listing.PriceTo     = dto.PriceTo;
        listing.UpdatedAt   = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── UPDATE STATUS ──────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> UpdateStatusAsync(
        int listingId, string userId, string statusStr)
    {
        if (!Enum.TryParse<ListingStatus>(statusStr, ignoreCase: true, out var newStatus))
            return (false, $"Nepoznat status '{statusStr}'. Dozvoljeni: Active, Paused, Archived.");

        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo izmene.");

        listing.Status    = newStatus;
        listing.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── DELETE (arhiviranje) ───────────────────────────────────────────────
    /// <summary>
    /// "Brisanje" = postavljanje Status = Archived.
    /// Fizičko brisanje se ne radi — čuvamo istoriju.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteAsync(int listingId, string userId)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo brisanja.");

        listing.Status    = ListingStatus.Archived;
        listing.UpdatedAt = DateTime.UtcNow;

        // Smanji TotalListings na provider profilu
        var provider = await db.ProviderProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId);
        if (provider is not null && provider.TotalListings > 0)
            provider.TotalListings--;

        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── UPLOAD IMAGE ───────────────────────────────────────────────────────
    /// <summary>
    /// Dodaje sliku na listing. Max 5 slika po listingu.
    /// Fajl se čuva u wwwroot/uploads/listings/{listingId}/.
    /// </summary>
    public async Task<(ListingImageDto? Result, string? Error)> UploadImageAsync(
        int listingId, string userId, IFormFile file)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (null, "Listing nije pronađen ili nemate pravo izmene.");

        // Učitaj slike
        var existingImages = await db.ListingImages
            .Where(i => i.ListingId == listingId)
            .ToListAsync();

        if (existingImages.Count >= 5)
            return (null, "Listing može imati maksimalno 5 slika.");

        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (!allowed.Contains(file.ContentType))
            return (null, "Dozvoljeni formati: JPEG, PNG, WebP.");

        const long maxBytes = 10 * 1024 * 1024; // 10 MB
        if (file.Length > maxBytes)
            return (null, "Slika ne sme biti veća od 10 MB.");

        // Putanja: wwwroot/uploads/listings/{listingId}/{guid}.{ext}
        var ext       = Path.GetExtension(file.FileName).ToLowerInvariant();
        var fileName  = $"{Guid.NewGuid():N}{ext}";
        var uploadDir = Path.Combine(env.WebRootPath, "uploads", "listings", listingId.ToString());
        Directory.CreateDirectory(uploadDir);

        var filePath = Path.Combine(uploadDir, fileName);
        await using (var stream = File.Create(filePath))
            await file.CopyToAsync(stream);

        // Relativna putanja — pun URL sastavlja MediaUrlJsonModifier pri
        // serijalizaciji, pa promena domena ne kvari postojeće slike.
        var imageUrl = $"/uploads/listings/{listingId}/{fileName}";

        var sortOrder = existingImages.Count > 0
            ? existingImages.Max(i => i.SortOrder) + 1
            : 0;

        var image = new ListingImage
        {
            ListingId = listingId,
            ImageUrl  = imageUrl,
            SortOrder = sortOrder
        };

        db.ListingImages.Add(image);
        listing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return (new ListingImageDto
        {
            Id        = image.Id,
            ImageUrl  = image.ImageUrl,
            SortOrder = image.SortOrder
        }, null);
    }

    // ── DELETE IMAGE ───────────────────────────────────────────────────────
    public async Task<(bool Success, string? Error)> DeleteImageAsync(
        int listingId, int imageId, string userId)
    {
        var listing = await GetOwnedListingAsync(listingId, userId);
        if (listing is null)
            return (false, "Listing nije pronađen ili nemate pravo izmene.");

        var image = await db.ListingImages
            .FirstOrDefaultAsync(i => i.Id == imageId && i.ListingId == listingId);

        if (image is null)
            return (false, "Slika nije pronađena.");

        // Pokušaj brisanje fajla sa diska
        try
        {
            var localPath = Path.Combine(
                env.WebRootPath, "uploads", "listings",
                listingId.ToString(),
                Path.GetFileName(image.ImageUrl));
            if (File.Exists(localPath)) File.Delete(localPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nije moguće obrisati fajl slike {ImageId}", imageId);
        }

        db.ListingImages.Remove(image);
        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── GET BY PROVIDER ────────────────────────────────────────────────────
    public async Task<List<ListingDto>> GetByProviderAsync(string userId)
    {
        return await db.Listings
            .AsNoTracking()
            .Include(l => l.Category)
            .Include(l => l.Images.OrderBy(i => i.SortOrder))
            .Include(l => l.ProviderProfile)
                .ThenInclude(pp => pp.User)
            .Where(l => l.ProviderProfile.UserId == userId &&
                        l.Status != ListingStatus.Archived)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => MapToDto(l))
            .ToListAsync();
    }

    // ── HELPERS ────────────────────────────────────────────────────────────

    /// <summary>Vraća listing samo ako mu je userId vlasnik.</summary>
    private async Task<Listing?> GetOwnedListingAsync(int listingId, string userId)
    {
        return await db.Listings
            .Include(l => l.ProviderProfile)
            .FirstOrDefaultAsync(l =>
                l.Id == listingId &&
                l.ProviderProfile.UserId == userId &&
                l.Status != ListingStatus.Archived);
    }

    private static string? ValidatePrice(
        PriceMode mode, decimal? fixed_, decimal? from, decimal? to)
    {
        return mode switch
        {
            PriceMode.Fixed      when fixed_ is null or <= 0
                => "Za fiksu cenu, unesi pozitivan iznos u polju fixedPrice.",
            PriceMode.Range      when from is null || to is null || from <= 0 || to <= 0
                => "Za cenovni raspon, unesi priceFrom i priceTo (oba pozitivna).",
            PriceMode.Range      when from >= to
                => "priceFrom mora biti manji od priceTo.",
            _   => null
        };
    }

    private static ListingDto MapToDto(Listing l) => new()
    {
        Id           = l.Id,
        Title        = l.Title,
        Description  = l.Description,
        Location     = l.Location,
        PriceMode    = l.PriceMode,
        FixedPrice   = l.FixedPrice,
        PriceFrom    = l.PriceFrom,
        PriceTo      = l.PriceTo,
        Status       = l.Status,
        ViewCount    = l.ViewCount,
        IsBoosted    = l.IsBoosted,
        CreatedAt    = l.CreatedAt,
        UpdatedAt    = l.UpdatedAt,
        CategoryId   = l.CategoryId,
        CategoryName = l.Category?.Name  ?? string.Empty,
        CategorySlug = l.Category?.Slug  ?? string.Empty,
        Images       = l.Images?
            .OrderBy(i => i.SortOrder)
            .Select(i => new ListingImageDto
            {
                Id        = i.Id,
                ImageUrl  = i.ImageUrl,
                SortOrder = i.SortOrder
            })
            .ToList() ?? [],
        Provider = l.ProviderProfile is null ? null! : new ProviderSummaryDto
        {
            ProviderProfileId = l.ProviderProfile.Id,
            UserId            = l.ProviderProfile.UserId,
            FullName          = l.ProviderProfile.User?.FullName ?? string.Empty,
            Profession        = l.ProviderProfile.Profession,
            ProfileImageUrl   = l.ProviderProfile.User?.ProfileImageUrl,
            AverageRating     = l.ProviderProfile.AverageRating,
            TotalReviews      = l.ProviderProfile.TotalReviews,
            IsVerified        = l.ProviderProfile.IsVerified
        }
    };
}
