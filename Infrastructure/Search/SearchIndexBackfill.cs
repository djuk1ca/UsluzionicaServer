using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Popunjava Search* kolone za redove koji su indeksirani starom verzijom
/// pravila (ili nikad).
///
/// Zašto uopšte postoji: SQL Server ne može da izvrši C# funkciju Fold(), pa
/// se migracija ne može napisati kao SQL UPDATE. Alternativa bi bila lanac od
/// ~40 ugnježdenih REPLACE-ova u T-SQL-u, koji bi bio collation-zavisan, ne bi
/// umeo NFD, i logika bi živela na dva mesta koja se tiho razilaze.
///
/// Zašto SearchVersion: kad se pravila preklapanja promene (nova slova, druga
/// mapa), dovoljno je podići SearchNormalizer.Version — sledeći start sam
/// re-indeksira sve. Bez toga bi svaka promena tražila ručnu intervenciju nad
/// produkcijskom bazom.
///
/// Idempotentno: ponovni poziv nad već indeksiranim redovima ne radi ništa.
/// </summary>
public static class SearchIndexBackfill
{
    /// <summary>
    /// Batch je namerno mali. Cilj nije brzina nego da se izbegne
    /// zaključavanje velikog broja redova u jednoj transakciji — na
    /// produkcijskoj bazi to bi blokiralo korisnike dok backfill traje.
    /// </summary>
    private const int BatchSize = 500;

    public static async Task RunAsync(
        AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var version  = SearchNormalizer.Version;
        var listings = 0;
        var users    = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Listings
                .Where(l => l.SearchVersion != version)
                .OrderBy(l => l.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            // SaveChangesAsync sam poziva SearchIndexer kroz RefreshSearchIndex,
            // pa je dovoljno označiti entitete kao izmenjene. Postavljamo
            // SearchVersion eksplicitno da bi EF video promenu i za redove
            // kojima se foldovani tekst slučajno ne menja.
            foreach (var listing in batch)
                listing.SearchVersion = version;

            await db.SaveChangesAsync(ct);

            // Bez ovoga change tracker raste kroz sve batch-eve i troši
            // memoriju linearno sa brojem redova.
            db.ChangeTracker.Clear();
            listings += batch.Count;
        }

        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Users
                .Where(u => u.SearchVersion != version)
                .OrderBy(u => u.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var user in batch)
                user.SearchVersion = version;

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            users += batch.Count;
        }

        if (listings + users > 0)
            logger.LogInformation(
                "Indeks pretrage v{Version}: re-indeksirano {Listings} oglasa i {Users} korisnika.",
                version, listings, users);
    }
}
