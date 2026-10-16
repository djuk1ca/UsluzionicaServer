using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.Services;

/// <summary>
/// Prijave oglasa, poslova i korisnika, i njihovo rešavanje.
///
/// RED ZA ADMINA JE PO METI, NE PO PRIJAVI.
/// Deset prijava istog oglasa je jedan posao i jedna odluka, ne deset. Zato je
/// red grupisan upit, a rešavanje zatvara sve prijave za tu metu odjednom.
///
/// BROJ PRIJAVA SE NE DENORMALIZUJE.
/// Ne postoji <c>Listing.ReportCount</c>. Broj se računa agregatom nad
/// <c>Reports</c> pri svakom otvaranju reda. Denormalizovan brojač bi bio drugi
/// izvor istine o istom podatku, a dva izvora se raziđu — obično posle prve
/// izmene koja zaobiđe change tracker. Volumen ovde ni izbliza ne traži tu cenu.
///
/// SADRŽAJ SE NE SKRIVA AUTOMATSKI.
/// Nema praga posle kog oglas nestaje sam. To bi bio poklon konkurenciji: par
/// lažnih prijava obori tuđi oglas bez ijedne provere. Prva prijava stavlja
/// oglas u red; sklanja ga čovek.
/// </summary>
public sealed class ReportService(
    AppDbContext           db,
    UserModerationService  userModeration,
    NotificationService    notifications,
    JobService             jobs,
    ChatImageService       chatImages,
    ILogger<ReportService> logger)
{
    // ── PRIJAVA ────────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> PrijaviAsync(
        string reporterId, CreateReportDto dto)
    {
        // Isto pravilo koje čuva i CHECK ograničenje u bazi, samo ranije i sa
        // razumljivom porukom. Bez ove provere korisnik bi dobio 500 iz SQL-a.
        var imaOglas     = dto.ListingId is not null;
        var imaPosao     = dto.JobId is not null;
        var imaKorisnika = !string.IsNullOrWhiteSpace(dto.ReportedUserId);

        if ((imaOglas ? 1 : 0) + (imaPosao ? 1 : 0) + (imaKorisnika ? 1 : 0) != 1)
            return (false, "Prijava mora imati tačno jednu metu.");

        if (dto.TargetType == ReportTargetType.Listing && !imaOglas)
            return (false, "Nedostaje oglas koji se prijavljuje.");

        if (dto.TargetType == ReportTargetType.Job && !imaPosao)
            return (false, "Nedostaje posao koji se prijavljuje.");

        if (dto.TargetType == ReportTargetType.User && !imaKorisnika)
            return (false, "Nedostaje korisnik koji se prijavljuje.");

        // ── Meta mora postojati, i ne sme biti sam prijavilac ──────────────
        string vlasnikId;

        if (dto.TargetType == ReportTargetType.Listing)
        {
            var oglas = await db.Listings
                .Where(l => l.Id == dto.ListingId)
                .Select(l => new { l.Id, VlasnikId = l.ProviderProfile.UserId })
                .FirstOrDefaultAsync();

            if (oglas is null)
                return (false, "Oglas nije pronađen.");

            vlasnikId = oglas.VlasnikId;
        }
        else if (dto.TargetType == ReportTargetType.Job)
        {
            var autor = await db.Jobs
                .Where(j => j.Id == dto.JobId && j.Status != JobStatus.Deleted)
                .Select(j => j.AuthorId)
                .FirstOrDefaultAsync();

            if (autor is null)
                return (false, "Posao nije pronađen.");

            vlasnikId = autor;
        }
        else
        {
            var postoji = await db.Users.AnyAsync(u => u.Id == dto.ReportedUserId);
            if (!postoji)
                return (false, "Korisnik nije pronađen.");

            vlasnikId = dto.ReportedUserId!;
        }

        if (vlasnikId == reporterId)
            return (false, "Ne možete prijaviti sopstveni sadržaj.");

        // „Prijavi sliku" iz četa: poruka mora biti od prijavljenog, a
        // prijavilac učesnik razgovora — inače bi se tuđa poruka zadržala.
        if (dto.MessageId is int porukaId)
        {
            var poruka = await db.Messages.AsNoTracking()
                .Where(m => m.Id == porukaId)
                .Select(m => new { m.SenderId, m.Conversation.User1Id, m.Conversation.User2Id })
                .FirstOrDefaultAsync();

            if (dto.TargetType != ReportTargetType.User || poruka is null || poruka.SenderId != vlasnikId
                || (poruka.User1Id != reporterId && poruka.User2Id != reporterId))
                return (false, "Poruka nije pronađena.");
        }

        db.Reports.Add(new Report
        {
            ReporterId     = reporterId,
            TargetType     = dto.TargetType,
            ListingId      = dto.TargetType == ReportTargetType.Listing ? dto.ListingId : null,
            JobId          = dto.TargetType == ReportTargetType.Job     ? dto.JobId : null,
            ReportedUserId = dto.TargetType == ReportTargetType.User    ? dto.ReportedUserId : null,
            MessageId      = dto.MessageId,
            Reason         = dto.Reason,
            Note           = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(),
            Status         = ReportStatus.Pending,
            CreatedAt      = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Filtrirani UNIQUE indeks: isti prijavilac, ista meta, prijava koja
            // još čeka. Namerno se ne pravi drugi red — inače bi jedan korisnik
            // uzastopnim prijavama sam podigao metu na vrh reda.
            return (false, "Već ste prijavili ovaj sadržaj. Prijava se razmatra.");
        }

        // Slika iz prijavljene poruke ostaje adminu i posle roka od 14 dana.
        if (dto.MessageId is int zadrzi)
            await chatImages.ZadrziAsync(zadrzi);

        logger.LogInformation(
            "Nova prijava: {TargetType} od {ReporterId}, razlog {Reason}",
            dto.TargetType, reporterId, dto.Reason);

        // Prijavljeni se NAMERNO ne obaveštava. Vidi NotificationKind.
        return (true, null);
    }

    // ── RED ZA ADMINA ──────────────────────────────────────────────────────

    /// <summary>
    /// Mete sa nerešenim prijavama, poređane po važnosti.
    ///
    /// Dva kriterijuma, ne jedan: prvo broj prijava, pa starost najstarije.
    /// Samo po broju, usamljena prijava od pre nedelju dana nikad ne bi stigla
    /// na red — a upravo ona može biti tačna.
    /// </summary>
    public async Task<List<ReportQueueItemDto>> RedAsync(
        ReportStatus status = ReportStatus.Pending)
    {
        // Dva odvojena upita umesto jednog sa uslovnim grupisanjem: oglasi se
        // grupišu po int ključu, korisnici po string ključu, a LINQ ne može da
        // grupiše po dve različite vrste u jednom prolazu bez ružnih trikova
        // koje EF ionako ne bi preveo u SQL.

        var oglasi = await db.Reports
            .AsNoTracking()
            .Where(r => r.Status == status && r.ListingId != null)
            .GroupBy(r => r.ListingId!.Value)
            .Select(g => new
            {
                ListingId   = g.Key,
                Broj        = g.Count(),
                Najstarija  = g.Min(r => r.CreatedAt),
                Razlozi     = g.Select(r => r.Reason).Distinct().ToList()
            })
            .ToListAsync();

        var poslovi = await db.Reports
            .AsNoTracking()
            .Where(r => r.Status == status && r.JobId != null)
            .GroupBy(r => r.JobId!.Value)
            .Select(g => new
            {
                JobId      = g.Key,
                Broj       = g.Count(),
                Najstarija = g.Min(r => r.CreatedAt),
                Razlozi    = g.Select(r => r.Reason).Distinct().ToList()
            })
            .ToListAsync();

        var korisnici = await db.Reports
            .AsNoTracking()
            .Where(r => r.Status == status && r.ReportedUserId != null)
            .GroupBy(r => r.ReportedUserId!)
            .Select(g => new
            {
                UserId     = g.Key,
                Broj       = g.Count(),
                Najstarija = g.Min(r => r.CreatedAt),
                Razlozi    = g.Select(r => r.Reason).Distinct().ToList()
            })
            .ToListAsync();

        var rezultat = new List<ReportQueueItemDto>();

        if (oglasi.Count > 0)
        {
            var ids = oglasi.Select(o => o.ListingId).ToList();

            var podaci = await db.Listings
                .AsNoTracking()
                .Where(l => ids.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    l.Title,
                    l.Status,
                    l.ModerationState,
                    VlasnikId  = l.ProviderProfile.UserId,
                    VlasnikIme = l.ProviderProfile.User.FullName
                })
                .ToDictionaryAsync(x => x.Id);

            foreach (var o in oglasi)
            {
                if (!podaci.TryGetValue(o.ListingId, out var p))
                    continue;   // oglas obrisan iz baze — prijava ostaje kao trag

                rezultat.Add(new ReportQueueItemDto
                {
                    TargetType        = ReportTargetType.Listing,
                    ListingId         = o.ListingId,
                    TargetNaziv       = p.Title,
                    VlasnikId         = p.VlasnikId,
                    VlasnikIme        = p.VlasnikIme,
                    BrojPrijava       = o.Broj,
                    NajstarijaPrijava = o.Najstarija,
                    Razlozi           = [.. o.Razlozi.Select(r => r.ToString())],
                    ModerationState   = p.ModerationState.ToString(),
                    VecObradjeno      = p.Status == ListingStatus.Archived
                });
            }
        }

        if (poslovi.Count > 0)
        {
            var ids = poslovi.Select(p => p.JobId).ToList();

            var podaci = await db.Jobs
                .AsNoTracking()
                .Where(j => ids.Contains(j.Id))
                .Select(j => new
                {
                    j.Id,
                    j.Title,
                    j.Status,
                    j.ModerationState,
                    j.AuthorId,
                    AutorIme = j.Author.FullName
                })
                .ToDictionaryAsync(x => x.Id);

            foreach (var o in poslovi)
            {
                if (!podaci.TryGetValue(o.JobId, out var p))
                    continue;

                rezultat.Add(new ReportQueueItemDto
                {
                    TargetType        = ReportTargetType.Job,
                    JobId             = o.JobId,
                    TargetNaziv       = p.Title,
                    VlasnikId         = p.AuthorId,
                    VlasnikIme        = p.AutorIme,
                    BrojPrijava       = o.Broj,
                    NajstarijaPrijava = o.Najstarija,
                    Razlozi           = [.. o.Razlozi.Select(r => r.ToString())],
                    ModerationState   = p.ModerationState.ToString(),
                    VecObradjeno      = p.Status == JobStatus.Deleted
                });
            }
        }

        if (korisnici.Count > 0)
        {
            var ids = korisnici.Select(k => k.UserId).ToList();

            var podaci = await db.Users
                .AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName, u.IsActive })
                .ToDictionaryAsync(x => x.Id);

            foreach (var k in korisnici)
            {
                if (!podaci.TryGetValue(k.UserId, out var p))
                    continue;

                rezultat.Add(new ReportQueueItemDto
                {
                    TargetType        = ReportTargetType.User,
                    ReportedUserId    = k.UserId,
                    TargetNaziv       = p.FullName,
                    VlasnikId         = k.UserId,
                    VlasnikIme        = p.FullName,
                    BrojPrijava       = k.Broj,
                    NajstarijaPrijava = k.Najstarija,
                    Razlozi           = [.. k.Razlozi.Select(r => r.ToString())],
                    VecObradjeno      = !p.IsActive
                });
            }
        }

        return [.. rezultat
            .OrderByDescending(x => x.BrojPrijava)
            .ThenBy(x => x.NajstarijaPrijava)];
    }

    /// <summary>Pojedinačne prijave za jednu metu — kad admin razvije stavku.</summary>
    public async Task<List<ReportDetailDto>> DetaljiAsync(
        ReportTargetType targetType, int? listingId, string? reportedUserId, int? jobId = null)
        => await db.Reports
            .AsNoTracking()
            .Where(Meta(targetType, listingId, jobId, reportedUserId))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new ReportDetailDto
            {
                Id          = r.Id,
                ReporterIme = r.Reporter.FullName,
                Razlog      = r.Reason.ToString(),
                Napomena    = r.Note,
                Status      = r.Status.ToString(),
                CreatedAt   = r.CreatedAt,
                ResolvedBy  = r.ResolvedBy != null ? r.ResolvedBy.FullName : null,
                ResolvedAt  = r.ResolvedAt,
                MessageId   = r.MessageId
            })
            .ToListAsync();

    // ── REŠAVANJE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Jedna odluka zatvara SVE nerešene prijave za tu metu.
    ///
    /// Admin je pregledao sadržaj, ne pojedinačnu prijavu — pa ostavljanje
    /// ostalih prijava otvorenim znači da se ista meta vraća u red i pregleda
    /// ponovo, bez novog povoda.
    /// </summary>
    public async Task<(bool Success, string? Error)> ResiAsync(
        string adminId, ResolveReportDto dto)
    {
        var jeOglas = dto.TargetType == ReportTargetType.Listing;
        var jePosao = dto.TargetType == ReportTargetType.Job;

        if (jeOglas && dto.ListingId is null)
            return (false, "Nedostaje oglas.");

        if (jePosao && dto.JobId is null)
            return (false, "Nedostaje posao.");

        if (!jeOglas && !jePosao && string.IsNullOrWhiteSpace(dto.ReportedUserId))
            return (false, "Nedostaje korisnik.");

        var meta = Meta(dto.TargetType, dto.ListingId, dto.JobId, dto.ReportedUserId);

        var imaPrijava = await db.Reports
            .Where(r => r.Status == ReportStatus.Pending)
            .AnyAsync(meta);

        if (!imaPrijava)
            return (false, "Nema nerešenih prijava za ovu metu.");

        // ── Radnja nad sadržajem ──────────────────────────────────────────
        switch (dto.Action)
        {
            case ReportAction.UkloniOglas:
            {
                if (!jeOglas)
                    return (false, "Uklanjanje oglasa nije moguće na prijavi korisnika.");

                var oglas = await db.Listings
                    .Where(l => l.Id == dto.ListingId)
                    .Select(l => new { l.Id, l.Title, VlasnikId = l.ProviderProfile.UserId })
                    .FirstOrDefaultAsync();

                if (oglas is null)
                    return (false, "Oglas nije pronađen.");

                await db.Listings
                    .Where(l => l.Id == dto.ListingId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(l => l.Status,          ListingStatus.Archived)
                        .SetProperty(l => l.ModerationState, ModerationState.Removed)
                        .SetProperty(l => l.UpdatedAt,       DateTime.UtcNow));

                await notifications.SendAsync(
                    oglas.VlasnikId,
                    NotificationKind.ListingRemoved,
                    "Oglas je uklonjen",
                    $"Oglas „{oglas.Title}\" je uklonjen jer krši uslove korišćenja." +
                    (string.IsNullOrWhiteSpace(dto.Note) ? "" : $" Razlog: {dto.Note}"),
                    oglas.Id);

                logger.LogWarning(
                    "Admin {AdminId} uklonio oglas {ListingId} po prijavi",
                    adminId, dto.ListingId);
                break;
            }

            case ReportAction.UkloniPosao:
            {
                if (!jePosao)
                    return (false, "Uklanjanje posla je moguće samo na prijavi posla.");

                var posao = await db.Jobs
                    .Where(j => j.Id == dto.JobId)
                    .Select(j => new { j.Id, j.Title, j.AuthorId, j.Status })
                    .FirstOrDefaultAsync();

                if (posao is null)
                    return (false, "Posao nije pronađen.");

                // Oznaka pre brisanja; red ostaje kao trag (prijavljen posao
                // se ne briše trajno — vidi JobService.TrajnoObrisiAsync).
                await db.Jobs
                    .Where(j => j.Id == dto.JobId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.ModerationState, ModerationState.Removed));

                if (posao.Status is not (JobStatus.Deleted or JobStatus.Completed))
                    await jobs.DeleteAsync(posao.Id, posao.AuthorId);

                await notifications.SendAsync(
                    posao.AuthorId,
                    NotificationKind.JobRemoved,
                    "Posao je uklonjen",
                    $"Posao „{posao.Title}\" je uklonjen jer krši uslove korišćenja." +
                    (string.IsNullOrWhiteSpace(dto.Note) ? "" : $" Razlog: {dto.Note}"),
                    posao.Id);

                logger.LogWarning(
                    "Admin {AdminId} uklonio posao {JobId} po prijavi",
                    adminId, dto.JobId);
                break;
            }

            case ReportAction.DeaktivirajNalog:
            {
                // Vlasnik se traži iz mete, a ne prima iz zahteva: klijent ne
                // sme da odredi ČIJI se nalog gasi. Inače bi izmenjen zahtev
                // gasio bilo koga.
                string vlasnikId;

                if (jeOglas)
                {
                    var v = await db.Listings
                        .Where(l => l.Id == dto.ListingId)
                        .Select(l => l.ProviderProfile.UserId)
                        .FirstOrDefaultAsync();

                    if (v is null)
                        return (false, "Oglas nije pronađen.");

                    vlasnikId = v;
                }
                else if (jePosao)
                {
                    var v = await db.Jobs
                        .Where(j => j.Id == dto.JobId)
                        .Select(j => j.AuthorId)
                        .FirstOrDefaultAsync();

                    if (v is null)
                        return (false, "Posao nije pronađen.");

                    vlasnikId = v;
                }
                else
                {
                    vlasnikId = dto.ReportedUserId!;
                }

                var (ok, greska) = await userModeration.DeaktivirajAsync(vlasnikId, dto.Note);
                if (!ok)
                    return (false, greska);

                logger.LogWarning(
                    "Admin {AdminId} deaktivirao nalog {UserId} po prijavi",
                    adminId, vlasnikId);
                break;
            }

            case ReportAction.Odbij:
            {
                // Sadržaj ostaje netaknut. Jedino se oglas obeležava kao
                // pregledan, da se ponovljeni talas prijava na isti oglas
                // prepozna kao maltretiranje a ne kao nov slučaj.
                if (jeOglas)
                {
                    await db.Listings
                        .Where(l => l.Id == dto.ListingId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(l => l.ModerationState, ModerationState.Cleared));
                }
                else if (jePosao)
                {
                    await db.Jobs
                        .Where(j => j.Id == dto.JobId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.ModerationState, ModerationState.Cleared));
                }

                logger.LogInformation(
                    "Admin {AdminId} odbio prijave za {TargetType}",
                    adminId, dto.TargetType);
                break;
            }

            default:
                return (false, "Nepoznata radnja.");
        }

        // ── Zatvaranje prijava ────────────────────────────────────────────
        var noviStatus = dto.Action == ReportAction.Odbij
            ? ReportStatus.Rejected
            : ReportStatus.Accepted;

        var sada = DateTime.UtcNow;

        // Odluka je doneta — zadržane slike iz prijavljenih poruka više ne
        // trebaju; sledeće noći odlaze sa porukom ako je rok prošao.
        var poruke = await db.Reports
            .Where(r => r.Status == ReportStatus.Pending && r.MessageId != null)
            .Where(meta)
            .Select(r => r.MessageId!.Value)
            .ToListAsync();

        await db.Reports
            .Where(r => r.Status == ReportStatus.Pending)
            .Where(meta)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status,         noviStatus)
                .SetProperty(r => r.ResolvedAt,     sada)
                .SetProperty(r => r.ResolvedById,   adminId)
                .SetProperty(r => r.ResolutionNote, dto.Note));

        await chatImages.OslobodiAsync(poruke);
        return (true, null);
    }

    /// <summary>Prijave za jednu metu — oglas, posao ili korisnik.</summary>
    private static Expression<Func<Report, bool>> Meta(
        ReportTargetType tip, int? listingId, int? jobId, string? userId) => tip switch
    {
        ReportTargetType.Listing => r => r.ListingId == listingId,
        ReportTargetType.Job     => r => r.JobId == jobId,
        _                        => r => r.ReportedUserId == userId
    };

    /// <summary>Broj nerešenih prijava — za značku na tabu u admin panelu.</summary>
    public async Task<int> BrojNeresenihAsync()
        => await db.Reports.CountAsync(r => r.Status == ReportStatus.Pending);
}
