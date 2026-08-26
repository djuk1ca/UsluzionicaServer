using StackExchange.Redis;

namespace UsluzionicaServer.Infrastructure.Redis;

/// <summary>
/// Distribuirani lock — obezbeđuje da posao izvrši TAČNO JEDNA instanca.
///
/// Zašto je potreban: `BackgroundService` se pokreće u svakom procesu. Sa dve
/// instance API-ja, `BoostExpiryService` bi u istom trenutku na obe krenuo da
/// oduzima BoostScore istim oglasima — pa bi svaki oglas bio umanjen dvaput.
/// `MessageCleanupService` bi dvaput brisao (bezopasno) ali bi i dvaput logovao
/// i dvaput opteretio bazu.
///
/// Mehanizam je Redis `SET key value NX PX ttl`:
///   NX  → upiši SAMO ako ključ ne postoji (atomično, na strani Redis-a)
///   PX  → automatski istekni posle ttl milisekundi
///
/// TTL je ono što ovo čini bezbednim. Ako instanca koja drži lock pukne ili joj
/// neko povuče struju, lock se sam oslobodi posle TTL-a. Bez TTL-a bi jedan pad
/// zauvek zaustavio taj posao na celom klasteru.
///
/// TTL zato mora biti DUŽI od najdužeg očekivanog trajanja posla. Ako posao
/// traje duže, drugi ga preuzme dok prvi još radi — i vraćamo se na problem
/// koji smo rešavali.
/// </summary>
public sealed class DistributedLock(
    RedisConnection            redis,
    ILogger<DistributedLock>   logger)
{
    /// <summary>
    /// Pokušava da zauzme lock. Vraća objekat koji ga oslobađa pri dispose-u,
    /// ili null ako ga druga instanca već drži.
    ///
    /// KAD REDIS NIJE DOSTUPAN vraća lock koji ništa ne radi — dakle posao se
    /// IZVRŠAVA. To je namerno: sa jednom instancom (razvoj, testovi, mali
    /// deployment) nema šta da se štiti, a odbijanje posla bi značilo da se
    /// boostovi nikad ne gase ako Redis ispadne.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl)
    {
        var db = redis.GetDatabase();

        if (db is null)
        {
            logger.LogDebug(
                "Redis nedostupan — posao '{Name}' se izvršava bez zaključavanja " +
                "(ispravno za jednu instancu).", name);
            return new NoOpLease();
        }

        // Nasumičan token identifikuje BAŠ NAŠ lock. Vidi Lease.DisposeAsync
        // za razlog zašto je neophodan.
        var token = Guid.NewGuid().ToString("N");
        var key   = $"{CacheService.Prefix}lock:{name}";

        try
        {
            var acquired = await db.StringSetAsync(key, token, ttl, When.NotExists);

            if (!acquired)
            {
                logger.LogInformation(
                    "Posao '{Name}' preskočen — druga instanca ga već izvršava.", name);
                return null;
            }

            logger.LogDebug("Lock '{Name}' zauzet na {Ttl}.", name, ttl);
            return new Lease(db, key, token, logger);
        }
        catch (Exception ex)
        {
            // Ista logika kao kad Redis nije konfigurisan: radije izvrši posao
            // dvaput nego nijednom.
            logger.LogWarning(ex,
                "Zauzimanje locka '{Name}' nije uspelo — izvršavam posao svejedno.", name);
            return new NoOpLease();
        }
    }

    private sealed class Lease(
        IDatabase db, string key, string token, ILogger logger) : IAsyncDisposable
    {
        /// <summary>
        /// Oslobađa lock, ali SAMO ako je i dalje naš.
        ///
        /// Zašto Lua skripta a ne prosto KeyDelete: zamisli da naš posao traje
        /// duže od TTL-a. Lock istekne, druga instanca ga zauzme, i tek onda mi
        /// završimo i pozovemo delete — obrisali bismo TUĐI lock i pustili treću
        /// instancu unutra. Poređenje i brisanje moraju biti jedna atomična
        /// operacija, a Redis to omogućava jedino kroz skriptu.
        /// </summary>
        private const string ReleaseScript = """
            if redis.call('GET', KEYS[1]) == ARGV[1] then
                return redis.call('DEL', KEYS[1])
            else
                return 0
            end
            """;

        public async ValueTask DisposeAsync()
        {
            try
            {
                var released = (int)(await db.ScriptEvaluateAsync(
                    ReleaseScript, [key], [token]));

                if (released == 0)
                    logger.LogWarning(
                        "Lock '{Key}' nije oslobođen — istekao je i verovatno ga drži " +
                        "druga instanca. Posao je trajao duže od TTL-a.", key);
            }
            catch (Exception ex)
            {
                // Ne bacamo iz Dispose-a. Lock ionako istekne sam.
                logger.LogWarning(ex, "Oslobađanje locka '{Key}' nije uspelo.", key);
            }
        }
    }

    /// <summary>Lock koji ne radi ništa — kad Redis nije u igri.</summary>
    private sealed class NoOpLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
