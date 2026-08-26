using StackExchange.Redis;

namespace UsluzionicaServer.Infrastructure.Redis;

/// <summary>
/// Obaveštava SVE instance da je neki keš zastareo — preko Redis pub/sub-a.
///
/// PROBLEM KOJI REŠAVA:
/// Neki keševi su u Redis-u i tamo ih je dovoljno obrisati jednom, jer ih sve
/// instance dele. Ali <see cref="Search.CategorySearchIndex"/> drži foldovana
/// imena kategorija U MEMORIJI SVAKOG PROCESA (namerno — pogađa se na svakom
/// tokenu svake pretrage, pa mrežni poziv po tokenu ne dolazi u obzir).
///
/// Kad admin preimenuje kategoriju, instanca koja je obradila taj zahtev očisti
/// SVOJU kopiju. Druga instanca ne zna ništa i nastavlja da pretražuje po starom
/// imenu — do isteka TTL-a od 10 minuta.
///
/// Pub/sub to rešava odmah: instanca koja je izmenila objavi poruku na kanal,
/// sve instance (uključujući nju samu) je prime i očiste svoju kopiju.
///
/// FALLBACK: ako Redis nije dostupan, poruka se ne pošalje i svaka instanca se
/// oslanja na svoj TTL. Zastarelost je tada ograničena na 10 minuta umesto na
/// milisekunde — sporije, ali nikad trajno pogrešno.
/// </summary>
public sealed class CacheInvalidator(
    RedisConnection             redis,
    ILogger<CacheInvalidator>   logger)
{
    /// <summary>Teme koje se objavljuju. Konstante da se ne prekucavaju.</summary>
    public static class Topics
    {
        public const string Categories = "categories";
    }

    private static RedisChannel Channel(string topic) =>
        RedisChannel.Literal($"{CacheService.Prefix}invalidate:{topic}");

    /// <summary>
    /// Objavljuje da je tema zastarela. Poruku primaju SVE instance, uključujući
    /// onu koja je objavila — zato rukovaoci moraju biti idempotentni.
    /// </summary>
    public async Task PublishAsync(string topic)
    {
        var sub = redis.GetSubscriber();

        if (sub is null)
        {
            logger.LogDebug(
                "Redis nedostupan — invalidacija '{Topic}' ostaje lokalna, " +
                "druge instance čekaju istek TTL-a.", topic);
            return;
        }

        try
        {
            var primljeno = await sub.PublishAsync(Channel(topic), DateTime.UtcNow.Ticks.ToString());
            logger.LogInformation(
                "Invalidacija '{Topic}' poslata na {Count} instanci.", topic, primljeno);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Objava invalidacije '{Topic}' nije uspela.", topic);
        }
    }

    /// <summary>
    /// Registruje rukovaoca koji se poziva kad bilo koja instanca objavi temu.
    ///
    /// StackExchange.Redis sam obnavlja pretplate posle prekida veze, pa ovo
    /// treba pozvati jednom pri startu. Ako Redis tada nije dostupan, pretplate
    /// nema i ostaje TTL kao jedina zaštita.
    /// </summary>
    public void Subscribe(string topic, Action handler)
    {
        var sub = redis.GetSubscriber();

        if (sub is null)
        {
            logger.LogDebug(
                "Redis nedostupan pri startu — nema pretplate na '{Topic}'.", topic);
            return;
        }

        try
        {
            sub.Subscribe(Channel(topic), (_, _) =>
            {
                try
                {
                    handler();
                    logger.LogDebug("Keš '{Topic}' poništen na osnovu poruke.", topic);
                }
                catch (Exception ex)
                {
                    // Izuzetak iz rukovaoca bi srušio nit pretplate i ugasio
                    // SVE buduće invalidacije na ovoj instanci.
                    logger.LogError(ex, "Rukovalac invalidacije '{Topic}' je pukao.", topic);
                }
            });

            logger.LogInformation("Pretplata na invalidaciju '{Topic}' aktivna.", topic);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pretplata na '{Topic}' nije uspela.", topic);
        }
    }
}
