using System.Text.Json;
using StackExchange.Redis;

namespace UsluzionicaServer.Infrastructure.Redis;

/// <summary>
/// Tanak sloj nad Redis-om za keširanje objekata.
///
/// Cela poenta ove klase je da pozivaoci NIKAD ne pišu try/catch oko keša.
/// Svaka metoda ovde guta grešku i ponaša se kao promašaj:
///
///   • Redis mrtav pri čitanju  → vrati null → pozivalac ide u bazu
///   • Redis mrtav pri upisu    → tiho ništa → sledeći zahtev opet ide u bazu
///
/// Rezultat je da gašenje Redis-a čini aplikaciju SPORIJOM, ne pokvarenom.
/// Vidi <see cref="RedisConnection"/> za detalje.
///
/// Zašto ručno preko IDatabase, a ne IDistributedCache: IDistributedCache
/// interfejs vraća bajtove i nema pojam o obrascu „uzmi ili izračunaj", pa bi
/// se ista try/catch logika ponavljala na svakom pozivnom mestu.
/// </summary>
public sealed class CacheService(
    RedisConnection         redis,
    ILogger<CacheService>   logger)
{
    /// <summary>
    /// Prefiks za SVE ključeve ove aplikacije.
    ///
    /// Bitno jer se Redis instanca u praksi deli između projekata. Bez prefiksa
    /// bi `categories:tree` iz dve aplikacije bio isti ključ — i jedna bi čitala
    /// tuđe podatke. Takođe omogućava `SCAN usluzionica:*` pri debagovanju.
    /// </summary>
    public const string Prefix = "usluzionica:";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Čita i deserijalizuje. Vraća null i pri promašaju i pri grešci.</summary>
    public async Task<T?> GetAsync<T>(string key) where T : class
    {
        var db = redis.GetDatabase();
        if (db is null) return null;

        try
        {
            var value = await db.StringGetAsync(Prefix + key);
            if (value.IsNullOrEmpty) return null;

            return JsonSerializer.Deserialize<T>(value!, JsonOpts);
        }
        catch (Exception ex)
        {
            // Obuhvata i RedisConnectionException i JsonException. Drugi se
            // dešava kad se oblik DTO-a promeni a stari zapis ostane u kešu —
            // tretiramo ga kao promašaj, ne kao grešku.
            logger.LogWarning(ex, "Čitanje iz keša nije uspelo za ključ '{Key}'.", key);
            return null;
        }
    }

    /// <summary>Serijalizuje i upisuje sa TTL-om. Greška se tiho ignoriše.</summary>
    public async Task SetAsync<T>(string key, T value, TimeSpan ttl) where T : class
    {
        var db = redis.GetDatabase();
        if (db is null) return;

        try
        {
            var json = JsonSerializer.Serialize(value, JsonOpts);
            await db.StringSetAsync(Prefix + key, json, ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upis u keš nije uspeo za ključ '{Key}'.", key);
        }
    }

    /// <summary>
    /// Glavni obrazac: vrati iz keša, ili izračunaj i zapamti.
    ///
    /// `factory` se poziva SAMO pri promašaju. Namerno NE postoji zaključavanje
    /// oko njega: ako dva zahteva istovremeno promaše, oba će otići u bazu i
    /// oba upisati isti rezultat. To je prihvatljivo za podatke koje keširamo
    /// (kategorije, profili) — a lock bi ovde bio distribuirani lock po ključu,
    /// što je više složenosti nego što jedan suvišan SELECT vredi.
    /// </summary>
    public async Task<T> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
        where T : class
    {
        var cached = await GetAsync<T>(key);
        if (cached is not null) return cached;

        // `where T : class` znači da je T ne-nullable, pa provera na null ovde
        // nije potrebna — a da stoji, kompajler bi zaključio suprotno i prijavio
        // CS8603 na `return fresh`.
        var fresh = await factory();
        await SetAsync(key, fresh, ttl);

        return fresh;
    }

    /// <summary>Briše jedan ključ — poziva se kad se podatak promeni.</summary>
    public async Task RemoveAsync(string key)
    {
        var db = redis.GetDatabase();
        if (db is null) return;

        try
        {
            await db.KeyDeleteAsync(Prefix + key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Brisanje ključa '{Key}' iz keša nije uspelo.", key);
        }
    }

    /// <summary>
    /// Briše sve ključeve po šablonu, npr. `provider:*`.
    ///
    /// Koristi SCAN, ne KEYS. KEYS blokira ceo Redis dok prolazi kroz sve
    /// ključeve — na produkcijskoj instanci to je zastoj koji se vidi.
    /// SCAN ide u komadima i pušta druge komande između.
    /// </summary>
    public async Task RemoveByPrefixAsync(string keyPrefix)
    {
        if (!redis.IsAvailable || redis.Multiplexer is null) return;

        try
        {
            var full = Prefix + keyPrefix + "*";

            foreach (var endpoint in redis.Multiplexer.GetEndPoints())
            {
                var server = redis.Multiplexer.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica) continue;

                var db = redis.Multiplexer.GetDatabase();

                await foreach (var key in server.KeysAsync(pattern: full, pageSize: 250))
                    await db.KeyDeleteAsync(key);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Brisanje po prefiksu '{Prefix}' nije uspelo.", keyPrefix);
        }
    }

    // ── Ključevi na jednom mestu ───────────────────────────────────────────
    // Da se ne prekucavaju po servisima — tipfeler u ključu ne pravi grešku,
    // nego tih promašaj keša koji niko ne primeti.
    public static class Keys
    {
        public const string CategoryTree = "categories:tree";

        public static string ProviderProfile(int providerProfileId) =>
            $"provider:{providerProfileId}";

        public const string ProviderPrefix = "provider:";
    }
}
