using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services.Jobs;

/// <summary>
/// Ono što se poslovima dešava protokom vremena. Pokreće ga
/// <see cref="Infrastructure.JobCleanupService"/>; odvojeno od pozadinskog
/// servisa da bi se moglo pozvati iz testa.
///
///   1. 24 h pre isteka   → autoru „ističe sutra" (jednom po roku)
///   2. rok prošao        → Istekao; 7 dana za „Produži"
///   3. 7 dana od izbora  → autoru „možeš da označiš izvršen" (jednom po dogovoru)
///   –. isticanje prošlo → gasi se zastavica
///   4. PurgeAt prošao    → trajno brisanje (osim prijavljenih)
/// </summary>
public sealed class JobOdrzavanje(
    AppDbContext           db,
    JobPravila             pravila,
    JobService             jobs,
    NotificationService    notifications,
    ILogger<JobOdrzavanje> logger)
{
    public sealed record Ishod(int Istice, int Isteklo, int Spremno, int Obrisano);

    public async Task<Ishod> ProdjiAsync(CancellationToken ct = default)
    {
        var sada = DateTime.UtcNow;

        // ── 1. Ističe sutra ────────────────────────────────────────────────
        var istice = await db.Jobs
            .Where(j => j.Status == JobStatus.Open && j.ExpiryNotifiedAt == null
                     && j.ExpiresAt > sada && j.ExpiresAt <= sada.AddDays(1))
            .Select(j => new { j.Id, j.AuthorId, j.Title })
            .ToListAsync(ct);

        foreach (var j in istice)
        {
            // Uslovni upis pre slanja — dve instance ne šalju isto dvaput.
            var oznaceno = await db.Jobs.Where(x => x.Id == j.Id && x.ExpiryNotifiedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiryNotifiedAt, sada), ct);
            if (oznaceno == 1)
                await notifications.SendAsync(j.AuthorId, NotificationKind.JobExpiringSoon,
                    "Posao ističe sutra", $"„{j.Title}“ ističe sutra. Produži ga — besplatno je.", j.Id);
        }

        // ── 2. Istekao ─────────────────────────────────────────────────────
        var isteklo = await db.Jobs
            .Where(j => j.Status == JobStatus.Open && j.ExpiresAt <= sada)
            .Select(j => new { j.Id, j.AuthorId, j.Title })
            .ToListAsync(ct);

        var brisanje = sada.AddDays(pravila.CekanjeIsteklog);
        foreach (var j in isteklo)
        {
            var oznaceno = await db.Jobs.Where(x => x.Id == j.Id && x.Status == JobStatus.Open && x.ExpiresAt <= sada)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, JobStatus.Expired)
                    .SetProperty(x => x.ExpiredAt, sada)
                    .SetProperty(x => x.PurgeAt, brisanje)
                    .SetProperty(x => x.UpdatedAt, sada), ct);
            if (oznaceno == 1)
                await notifications.SendAsync(j.AuthorId, NotificationKind.JobExpired,
                    "Posao je istekao",
                    $"„{j.Title}“ je istekao. Produži ga u narednih {pravila.CekanjeIsteklog} dana — javljanja su sačuvana.",
                    j.Id);
        }

        // ── 3. Može da se označi izvršen ───────────────────────────────────
        var granica = sada.AddDays(-pravila.CekanjeDoIzvrsenja);
        var spremno = await db.Jobs
            .Where(j => j.Status == JobStatus.Assigned && j.CompletableNotifiedAt == null
                     && j.AssignedAt != null && j.AssignedAt <= granica)
            .Select(j => new { j.Id, j.AuthorId, j.Title })
            .ToListAsync(ct);

        foreach (var j in spremno)
        {
            var oznaceno = await db.Jobs.Where(x => x.Id == j.Id && x.CompletableNotifiedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletableNotifiedAt, sada), ct);
            if (oznaceno == 1)
                await notifications.SendAsync(j.AuthorId, NotificationKind.JobCompletable,
                    "Ako je posao urađen, označi ga",
                    $"„{j.Title}“ — izvršilac dobija {pravila.NagradaIzvrsiocu:0.##} tokena, ti {pravila.NagradaAutoru:0.##}.",
                    j.Id);
        }

        // ── Isticanje prošlo ───────────────────────────────────────────────
        // Pretraga i kartica već gledaju BoostExpiresAt; ovo samo sređuje
        // zastavicu i skor da se novo isticanje ne bi sabiralo sa starim.
        // Istekao posao zadržava isticanje — „Produži" ga vraća ako još traje.
        await db.Jobs
            .Where(j => j.IsBoosted && j.BoostExpiresAt != null && j.BoostExpiresAt <= sada)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsBoosted, false)
                .SetProperty(x => x.BoostScore, 0m)
                .SetProperty(x => x.BoostExpiresAt, (DateTime?)null), ct);

        // ── 4. Trajno brisanje ─────────────────────────────────────────────
        var zaBrisanje = await db.Jobs
            .Where(j => j.PurgeAt != null && j.PurgeAt <= sada
                     && (j.Status == JobStatus.Expired || j.Status == JobStatus.Completed || j.Status == JobStatus.Deleted))
            .Select(j => j.Id)
            .ToListAsync(ct);

        var obrisano = 0;
        foreach (var id in zaBrisanje)
            if (await jobs.TrajnoObrisiAsync(id)) obrisano++;

        if (istice.Count + isteklo.Count + spremno.Count + obrisano > 0)
            logger.LogInformation(
                "Poslovi: ističe {Istice}, isteklo {Isteklo}, spremno za izvršen {Spremno}, obrisano {Obrisano}.",
                istice.Count, isteklo.Count, spremno.Count, obrisano);

        return new Ishod(istice.Count, isteklo.Count, spremno.Count, obrisano);
    }
}
