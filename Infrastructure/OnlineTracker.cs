using System.Collections.Concurrent;
using StackExchange.Redis;
using UsluzionicaServer.Infrastructure.Redis;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Praćenje online korisnika i njihovih SignalR konekcija.
///
/// ZAŠTO OVO MORA U REDIS:
/// SignalR konekcija je vezana za JEDAN proces. Sa dve instance API-ja iza load
/// balancera, Ana je konektovana na instancu A, Marko na instancu B. Kad Marko
/// otvori listu razgovora, njegov zahtev obrađuje instanca B — koja u svojoj
/// memoriji nema pojma o Aninoj konekciji i prikaže je kao offline. Uvek.
///
/// Redis drži to stanje van procesa, pa obe instance vide isto.
///
/// STRUKTURA: Redis SET po korisniku.
///     usluzionica:online:{userId} → { connectionId1, connectionId2, ... }
/// Skup jer isti korisnik može biti prijavljen sa telefona i sa laptopa
/// istovremeno — offline je tek kad NIJEDNA konekcija ne ostane.
///
/// TTL na ključu je zaštita od procesa koji padne: `OnDisconnectedAsync` se
/// tada nikad ne izvrši i connectionId bi zauvek ostao u skupu, pa bi korisnik
/// večno bio „online". TTL se obnavlja pri svakoj registraciji.
///
/// FALLBACK: bez Redis-a radi tačno kao pre — ConcurrentDictionary u memoriji.
/// To je ispravno za jednu instancu i drži testove zelenim.
/// </summary>
public sealed class OnlineTracker(
    RedisConnection         redis,
    ILogger<OnlineTracker>  logger)
{
    /// <summary>
    /// Duže od bilo kog razumnog SignalR keep-alive intervala. Kratak TTL bi
    /// korisnike prikazivao kao offline usred aktivnog razgovora.
    /// </summary>
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(12);

    // Rezerva kad Redis nije dostupan.
    private readonly ConcurrentDictionary<string, HashSet<string>> _local = new();
    private readonly object _localLock = new();

    private static string Key(string userId) => $"{CacheService.Prefix}online:{userId}";

    // ── Registracija ───────────────────────────────────────────────────────

    /// <summary>Registruje novu konekciju pri OnConnectedAsync.</summary>
    public async Task RegisterAsync(string userId, string connectionId)
    {
        var db = redis.GetDatabase();

        if (db is not null)
        {
            try
            {
                await db.SetAddAsync(Key(userId), connectionId);
                await db.KeyExpireAsync(Key(userId), KeyTtl);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Redis upis online statusa nije uspeo — koristim lokalnu memoriju.");
            }
        }

        lock (_localLock)
        {
            if (!_local.TryGetValue(userId, out var set))
            {
                set = [];
                _local[userId] = set;
            }
            set.Add(connectionId);
        }
    }

    /// <summary>
    /// Uklanja konekciju pri OnDisconnectedAsync.
    /// Vraća true ako je korisnik POTPUNO offline (nema više nijedne konekcije).
    /// </summary>
    public async Task<bool> UnregisterAsync(string userId, string connectionId)
    {
        var db = redis.GetDatabase();

        if (db is not null)
        {
            try
            {
                await db.SetRemoveAsync(Key(userId), connectionId);

                // SCARD posle SREM: ako je skup prazan, Redis sam briše ključ,
                // pa je i EXISTS false. Vraćamo true = korisnik je otišao.
                var preostalo = await db.SetLengthAsync(Key(userId));
                return preostalo == 0;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Redis brisanje online statusa nije uspelo — koristim lokalnu memoriju.");
            }
        }

        lock (_localLock)
        {
            if (!_local.TryGetValue(userId, out var set)) return true;

            set.Remove(connectionId);
            if (set.Count != 0) return false;

            _local.TryRemove(userId, out _);
            return true;
        }
    }

    // ── Čitanje ────────────────────────────────────────────────────────────

    public async Task<bool> IsOnlineAsync(string userId)
    {
        var db = redis.GetDatabase();

        if (db is not null)
        {
            try
            {
                return await db.KeyExistsAsync(Key(userId));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis provera online statusa nije uspela.");
            }
        }

        return _local.ContainsKey(userId);
    }

    /// <summary>
    /// Online status za VIŠE korisnika odjednom.
    ///
    /// Postoji zbog liste razgovora: ona prolazi kroz N sagovornika, i poziv
    /// <see cref="IsOnlineAsync"/> u petlji bi značio N odvojenih mrežnih
    /// obilazaka do Redis-a. Ovde sve ide kao jedan batch — biblioteka ih
    /// spakuje u jedan pipeline i čeka sve odgovore zajedno.
    /// </summary>
    public async Task<IReadOnlySet<string>> WhoIsOnlineAsync(IEnumerable<string> userIds)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return new HashSet<string>();

        var db = redis.GetDatabase();

        if (db is not null)
        {
            try
            {
                var batch = db.CreateBatch();
                var tasks = ids.ToDictionary(id => id, id => batch.KeyExistsAsync(Key(id)));
                batch.Execute();

                await Task.WhenAll(tasks.Values);

                return tasks.Where(kv => kv.Value.Result)
                            .Select(kv => kv.Key)
                            .ToHashSet();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis batch provera online statusa nije uspela.");
            }
        }

        return ids.Where(_local.ContainsKey).ToHashSet();
    }

    /// <summary>Snapshot connectionId-ova za datog korisnika.</summary>
    public async Task<IReadOnlyList<string>> GetConnectionsAsync(string userId)
    {
        var db = redis.GetDatabase();

        if (db is not null)
        {
            try
            {
                var members = await db.SetMembersAsync(Key(userId));
                return members.Select(m => m.ToString()).ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis čitanje konekcija nije uspelo.");
            }
        }

        lock (_localLock)
        {
            return _local.TryGetValue(userId, out var set) ? set.ToList() : [];
        }
    }
}
