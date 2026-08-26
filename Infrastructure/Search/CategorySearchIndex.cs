using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Foldovana imena kategorija, u memoriji.
///
/// Zašto ime kategorije NIJE denormalizovano u Listing.SearchTitle: kategorija
/// je zajednička za mnogo oglasa, pa bi admin preimenovanjem jedne kategorije
/// ustajao svaki oglas u njoj — a preimenovanje ne prolazi kroz oglase, pa bi
/// indeks tiho ostao pogrešan. 188 kategorija stane u memoriju bez problema.
///
/// Poklapanje je PO TOKENU, ne unija. Za upit „frizer beograd" token „frizer"
/// pogađa kategoriju, a „beograd" ne — pa oglas mora zadovoljiti oba uslova
/// zasebno. Da se vraćala unija, upit bi vratio sve iz te kategorije bez
/// obzira na grad.
///
/// Singleton sa TTL-om od 10 minuta. Podaci OSTAJU u memoriji svakog procesa
/// namerno: pogađa se na svakom tokenu svake pretrage, pa mrežni poziv po
/// tokenu ne dolazi u obzir.
///
/// Faza B je rešila deljenje: instanca koja izmeni kategorije objavi poruku
/// preko <see cref="CacheInvalidator"/>, i SVE instance odmah očiste svoju
/// kopiju. TTL ostaje kao rezerva kad Redis nije dostupan.
/// </summary>
public sealed class CategorySearchIndex
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CategorySearchIndex(IServiceScopeFactory scopeFactory, CacheInvalidator invalidator)
    {
        _scopeFactory = scopeFactory;

        // Pretplata pri startu: kad BILO KOJA instanca izmeni kategorije,
        // i ova očisti svoju kopiju u memoriji.
        invalidator.Subscribe(CacheInvalidator.Topics.Categories, Invalidate);
    }

    private sealed record Entry(int Id, string FoldedName, int? ParentId);

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Entry>? _entries;
    private DateTime     _loadedAt = DateTime.MinValue;

    /// <summary>
    /// Za svaki token upita vraća skup ID-jeva kategorija čije ime taj token
    /// pogađa. Ključ je <see cref="SearchQuery.Token.Value"/>.
    ///
    /// Pogođena roditeljska kategorija povlači i sve podkategorije — „beauty"
    /// treba da nađe i oglase iz „Frizerski saloni".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, HashSet<int>>> MatchPerTokenAsync(
        SearchQuery query, CancellationToken ct = default)
    {
        var result = new Dictionary<string, HashSet<int>>();
        if (query.IsEmpty) return result;

        var entries = await GetEntriesAsync(ct);

        foreach (var token in query.Tokens)
        {
            var matched = new HashSet<int>();

            foreach (var entry in entries)
                if (token.Variants.Any(v => entry.FoldedName.Contains(v, StringComparison.Ordinal)))
                    matched.Add(entry.Id);

            if (matched.Count > 0)
            {
                // Deca pogođenih roditelja.
                foreach (var entry in entries)
                    if (entry.ParentId is { } parentId && matched.Contains(parentId))
                        matched.Add(entry.Id);

                result[token.Value] = matched;
            }
        }

        return result;
    }

    /// <summary>Poništava keš — poziva se kad admin izmeni kategorije.</summary>
    public void Invalidate() => _loadedAt = DateTime.MinValue;

    private async Task<List<Entry>> GetEntriesAsync(CancellationToken ct)
    {
        if (_entries is not null && DateTime.UtcNow - _loadedAt < Ttl)
            return _entries;

        await _lock.WaitAsync(ct);
        try
        {
            // Druga nit je možda učitala dok smo čekali bravu.
            if (_entries is not null && DateTime.UtcNow - _loadedAt < Ttl)
                return _entries;

            // Singleton ne sme držati scoped DbContext — otud sopstveni scope.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var raw = await db.Categories
                .AsNoTracking()
                .Select(c => new { c.Id, c.Name, c.ParentId })
                .ToListAsync(ct);

            _entries  = raw.Select(c => new Entry(c.Id, SearchNormalizer.Fold(c.Name), c.ParentId))
                           .ToList();
            _loadedAt = DateTime.UtcNow;

            return _entries;
        }
        finally
        {
            _lock.Release();
        }
    }
}
