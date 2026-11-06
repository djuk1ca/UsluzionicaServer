using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Infrastructure.ChatMedia;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Rok čuvanja poruka (MessageRetentionDays, 14) — tekst I slike, i u bazi i
/// na disku. Pokreće ga <see cref="Infrastructure.MessageCleanupService"/>
/// svake noći; odvojeno da bi se moglo pozvati iz testa.
///
///   1. prilozi starih poruka: FAJL PA RED. Ako proces pukne između, ostaje
///      red bez fajla (sledeće noći se obriše), a ne fajl bez reda koji niko
///      ne vidi.
///   2. poruke — osim onih čiju sliku drži prijava (HeldUntil); kaskada bi
///      inače obrisala i red priloga.
///   3. siročići: u fasciklama dana starijih od roka + 1, svaki fajl čiji id
///      nije u tabeli (pad usred slanja, ručni restore baze).
/// </summary>
public sealed class PorukeCiscenje(
    AppDbContext            db,
    ChatMediaStore          store,
    IConfiguration          config,
    ILogger<PorukeCiscenje> logger)
{
    const int Serija = 500;

    public sealed record Ishod(int Slika, int Poruka, int Sirocica);

    public async Task<Ishod> PocistiAsync(DateTime? sadaUtc = null, CancellationToken ct = default)
    {
        var sada   = sadaUtc ?? DateTime.UtcNow;
        var dana   = config.GetValue("MessageRetentionDays", 14);
        var cutoff = sada.AddDays(-dana);

        // ── 1. Slike starih poruka ─────────────────────────────────────────
        var slika = 0;
        while (true)
        {
            var serija = await db.MessageAttachments
                .Where(a => a.Message.SentAt < cutoff && (a.HeldUntil == null || a.HeldUntil <= sada))
                .OrderBy(a => a.Id)
                .Take(Serija)
                .Select(a => new { a.Id, a.StoragePath })
                .ToListAsync(ct);
            if (serija.Count == 0) break;

            foreach (var p in serija)
                store.Obrisi(p.StoragePath);

            var ids = serija.Select(p => p.Id).ToList();
            slika += await db.MessageAttachments.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
        }

        // ── 2. Poruke ──────────────────────────────────────────────────────
        var poruka = await db.Messages
            .Where(m => m.SentAt < cutoff && !(m.Attachment != null && m.Attachment.HeldUntil > sada))
            .ExecuteDeleteAsync(ct);

        // ── 3. Siročići ────────────────────────────────────────────────────
        var sirocica = await SirociciAsync(sada.Date.AddDays(-(dana + 1)), ct);

        if (slika + poruka + sirocica > 0)
            logger.LogInformation(
                "Čišćenje poruka: {Poruka} poruka, {Slika} slika, {Sirocica} fajlova bez reda (rok {Dana} dana).",
                poruka, slika, sirocica, dana);

        return new Ishod(slika, poruka, sirocica);
    }

    async Task<int> SirociciAsync(DateTime doDana, CancellationToken ct)
    {
        if (!Directory.Exists(store.Koren)) return 0;
        var obrisano = 0;

        foreach (var dan in Dani(store.Koren).Where(d => d.Datum < doDana))
        {
            var fajlovi = Directory.GetFiles(dan.Putanja, "*.bin");
            var poId = fajlovi
                .Select(f => (Fajl: f, Id: Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var g) ? g : (Guid?)null))
                .ToList();

            var ids = poId.Where(x => x.Id is not null).Select(x => x.Id!.Value).ToList();
            var postoje = ids.Count == 0
                ? new HashSet<Guid>()
                : (await db.MessageAttachments.Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();

            foreach (var (fajl, id) in poId)
            {
                if (id is Guid g && postoje.Contains(g)) continue;
                try { File.Delete(fajl); obrisano++; }
                catch (Exception ex) { logger.LogWarning(ex, "Siroče {Fajl} nije obrisano.", fajl); }
            }

            ObrisiAkoJePrazno(dan.Putanja);
        }
        return obrisano;
    }

    /// <summary>Fascikle yyyy/MM/dd ispod korena, sa datumom iz imena.</summary>
    static IEnumerable<(DateTime Datum, string Putanja)> Dani(string koren)
    {
        foreach (var g in Directory.GetDirectories(koren))
        foreach (var m in Directory.GetDirectories(g))
        foreach (var d in Directory.GetDirectories(m))
        {
            if (int.TryParse(Path.GetFileName(g), out var god) && int.TryParse(Path.GetFileName(m), out var mes)
                && int.TryParse(Path.GetFileName(d), out var dan) && mes is >= 1 and <= 12 && dan >= 1 && dan <= DateTime.DaysInMonth(god, mes))
                yield return (new DateTime(god, mes, dan, 0, 0, 0, DateTimeKind.Utc), d);
        }
    }

    /// <summary>Dan, pa mesec, pa godina — tačno tri nivoa, nikad iznad korena.</summary>
    static void ObrisiAkoJePrazno(string dan)
    {
        try
        {
            var f = dan;
            for (var nivo = 0; nivo < 3 && f is not null; nivo++, f = Path.GetDirectoryName(f))
            {
                if (Directory.EnumerateFileSystemEntries(f).Any()) break;
                Directory.Delete(f);
            }
        }
        catch (IOException) { }
    }
}
