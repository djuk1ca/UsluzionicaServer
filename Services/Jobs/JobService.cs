using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Infrastructure.Search;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services.Jobs;

/// <summary>
/// Posao kao objava: objava, izmena, slike, brisanje, produženje, prikaz,
/// pretraga i „Moji poslovi". Javljanja i dogovor su u
/// <see cref="JobApplicationService"/>, istek i čišćenje u <see cref="JobOdrzavanje"/>.
///
/// Ceo životni ciklus i pravila: Obsidian „Plan — Poslovi i ocene korisnika
/// (v2)" §3; dizajn: design-system/poslovi.
/// </summary>
public sealed class JobService(
    AppDbContext            db,
    JobPravila              pravila,
    JobSlike                slike,
    CategorySearchIndex     categoryIndex,
    ImageModerationGate     imageGate,
    NotificationService     notifications,
    ILogger<JobService>     logger)
{
    private const string LikeEscape = "\\";

    /// <summary>Ispod ovoga pretraga uključuje i opis (skuplji sloj).</summary>
    private const int MinRezultata = 3;

    // ── OBJAVA ─────────────────────────────────────────────────────────────
    public async Task<(JobDto? Job, string? Error)> CreateAsync(string authorId, CreateJobDto dto)
    {
        var autor = await db.Users.FirstOrDefaultAsync(u => u.Id == authorId);
        if (autor is null || !autor.IsActive)
            return (null, "Nalog nije pronađen.");

        // Kao i za oglase i ocene: poslove objavljuju samo potvrđeni nalozi —
        // inače bi jednokratni nalozi punili pretragu.
        if (!autor.EmailConfirmed)
            return (null, "Potvrdi mejl da objaviš posao.");

        var aktivnih = await db.Jobs.CountAsync(j => j.AuthorId == authorId && j.Status == JobStatus.Open);
        if (aktivnih >= pravila.NajviseAktivnih)
            return (null, $"Imaš {pravila.NajviseAktivnih} aktivnih poslova. Završi, obriši ili sačekaj da neki istekne, pa objavi novi.");

        var greska = await ProveriKategorijuIGradAsync(dto.CategoryId, dto.Location);
        if (greska is not null) return (null, greska);

        var sada = DateTime.UtcNow;
        var job = new Job
        {
            AuthorId    = authorId,
            CategoryId  = dto.CategoryId,
            Location    = NormalizujGrad(dto.Location),
            Title       = dto.Title.Trim(),
            Description = dto.Description.Trim(),
            PayAmount   = dto.PayAmount,
            Status      = JobStatus.Open,
            ExpiresAt   = sada.AddDays(pravila.TrajanjeDana),
            CreatedAt   = sada,
            UpdatedAt   = sada
        };

        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        logger.LogInformation("Posao {JobId} objavljen (autor {AuthorId}).", job.Id, authorId);
        return (await GetAsync(job.Id, authorId), null);
    }

    // ── IZMENA ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Dok niko nije javio — menja se sve. Kad ima javljanja (ili je posao
    /// dogovoren) — samo opis i slike: uslugodavci su se javili na tu cenu i
    /// kategoriju (dizajn 5.9).
    /// </summary>
    public async Task<(bool Ok, string? Error)> UpdateAsync(int jobId, string authorId, UpdateJobDto dto)
    {
        var job = await db.Jobs
            .Include(j => j.Applications)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);

        if (job is null || job.Status is JobStatus.Completed or JobStatus.Deleted)
            return (false, "Posao nije pronađen.");
        if (job.ModerationState == ModerationState.Removed)
            return (false, "Posao je uklonjen zbog kršenja pravila.");

        var zakljucano = job.Status == JobStatus.Assigned || job.Applications.Any(a => JobMapa.Vazi(a.Status));

        if (!zakljucano)
        {
            if (dto.Title is { } naslov)
            {
                naslov = naslov.Trim();
                if (naslov.Length < 5) return (false, "Naslov mora imati bar 5 znakova.");
                job.Title = naslov;
            }

            if (dto.CategoryId is int kat || dto.Location is not null)
            {
                var greska = await ProveriKategorijuIGradAsync(dto.CategoryId ?? job.CategoryId, dto.Location ?? job.Location);
                if (greska is not null) return (false, greska);
                if (dto.CategoryId is int novaKat) job.CategoryId = novaKat;
                if (dto.Location is not null)      job.Location   = NormalizujGrad(dto.Location);
            }

            if (dto.PayNegotiable)              job.PayAmount = null;
            else if (dto.PayAmount is decimal i) job.PayAmount = i;
        }

        job.Description = dto.Description.Trim();
        job.UpdatedAt   = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── SLIKE ──────────────────────────────────────────────────────────────
    public async Task<(JobImageDto? Image, string? Error)> UploadImageAsync(int jobId, string authorId, IFormFile file)
    {
        var job = await db.Jobs.Include(j => j.Images)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);

        if (job is null || job.Status is JobStatus.Completed or JobStatus.Deleted)
            return (null, "Posao nije pronađen.");
        if (job.Images.Count >= pravila.NajviseSlika)
            return (null, $"Posao može imati najviše {pravila.NajviseSlika} fotografije.");

        // Isti put kao slike oglasa: format po sadržaju, pa automatska provera.
        var (ext, greska) = await ImageUploads.ValidateAsync(file, ImageUploads.MaxImageBytes);
        if (ext is null) return (null, greska);

        var (cista, greskaModeracije) = await imageGate.ProveriAsync(file, authorId, listingId: null, jobId: jobId);
        if (!cista) return (null, greskaModeracije);

        var ime = $"{Guid.NewGuid():N}{ext}";
        Directory.CreateDirectory(slike.Folder(jobId));
        await using (var s = File.Create(Path.Combine(slike.Folder(jobId), ime)))
            await file.CopyToAsync(s);

        var slika = new JobImage
        {
            JobId     = jobId,
            ImageUrl  = JobSlike.Url(jobId, ime),
            SortOrder = job.Images.Count == 0 ? 0 : job.Images.Max(i => i.SortOrder) + 1
        };
        db.JobImages.Add(slika);
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return (new JobImageDto { Id = slika.Id, ImageUrl = slika.ImageUrl, SortOrder = slika.SortOrder }, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteImageAsync(int jobId, int imageId, string authorId)
    {
        var slika = await db.JobImages
            .FirstOrDefaultAsync(i => i.Id == imageId && i.JobId == jobId && i.Job.AuthorId == authorId);
        if (slika is null) return (false, "Fotografija nije pronađena.");

        slike.ObrisiFajl(jobId, slika.ImageUrl);
        db.JobImages.Remove(slika);
        await db.SaveChangesAsync();
        return (true, null);
    }

    // ── PRODUŽENJE ─────────────────────────────────────────────────────────
    /// <summary>
    /// „Produži za 30 dana": aktivan posao u poslednja 3 dana roka, ili istekao
    /// dok još nije obrisan. Besplatno, koliko god puta.
    /// </summary>
    public async Task<(DateTime? ExpiresAt, string? Error)> ExtendAsync(int jobId, string authorId)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);
        var sada = DateTime.UtcNow;

        if (job is null || job.Status is JobStatus.Completed or JobStatus.Deleted)
            return (null, "Posao nije pronađen.");
        if (job.ModerationState == ModerationState.Removed)
            return (null, "Posao je uklonjen zbog kršenja pravila.");
        if (!JobMapa.MozeProduzenje(job, pravila, sada))
            return (null, job.Status == JobStatus.Assigned
                ? "Dogovoren posao ne ističe — rok stoji dok traje dogovor."
                : $"Produženje je moguće u poslednja {pravila.ProzorProduzenja} dana pre isteka.");

        job.Status           = JobStatus.Open;
        job.ExpiresAt        = sada.AddDays(pravila.TrajanjeDana);
        job.ExpiryNotifiedAt = null;
        job.ExpiredAt        = null;
        job.PurgeAt          = null;
        job.UpdatedAt        = sada;
        await db.SaveChangesAsync();

        return (job.ExpiresAt, null);
    }

    // ── BRISANJE ───────────────────────────────────────────────────────────
    /// <summary>
    /// Autor briše posao iz bilo kog stanja. Svi koji se još računaju (poslato
    /// ili izabrano) dobijaju obaveštenje; posao nestaje odmah.
    /// </summary>
    public async Task<(bool Ok, string? Error)> DeleteAsync(int jobId, string authorId)
    {
        var job = await db.Jobs
            .Include(j => j.Applications)
            .Include(j => j.Images)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);

        if (job is null || job.Status is JobStatus.Completed or JobStatus.Deleted)
            return (false, "Posao nije pronađen.");

        var obavesti = job.Applications
            .Where(a => JobMapa.Vazi(a.Status))
            .Select(a => a.ProviderUserId)
            .ToList();

        await OznaciObrisanAsync(job);

        foreach (var uslugodavac in obavesti)
            await notifications.SendAsync(uslugodavac, NotificationKind.JobRemoved,
                "Posao je uklonjen", $"Posao „{job.Title}“ je uklonjen.", job.Id);

        await TrajnoObrisiAsync(job.Id);
        return (true, null);
    }

    /// <summary>Brisanje naloga: svi autorovi poslovi kao „Obriši posao".</summary>
    public async Task ObrisiSveAutoroveAsync(string authorId)
    {
        var ids = await db.Jobs
            .Where(j => j.AuthorId == authorId && j.Status != JobStatus.Completed && j.Status != JobStatus.Deleted)
            .Select(j => j.Id)
            .ToListAsync();

        foreach (var id in ids)
            await DeleteAsync(id, authorId);
    }

    /// <summary>
    /// Posao postaje „obrisan": van svih lista, bez opisa i slika (lični
    /// podaci se ne drže duže nego što treba). Red ostaje dok ga
    /// <see cref="TrajnoObrisiAsync"/> ne ukloni.
    /// </summary>
    internal async Task OznaciObrisanAsync(Job job)
    {
        slike.ObrisiSve(job.Id);
        db.JobImages.RemoveRange(job.Images);

        job.Status      = JobStatus.Deleted;
        job.Description = string.Empty;
        job.IsBoosted   = false;
        job.PurgeAt     = DateTime.UtcNow;
        job.UpdatedAt   = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Trajno briše red — osim ako je posao prijavljen: prijava je revizijski
    /// trag (FK Restrict), pa takav posao ostaje kao skriven „obrisan" zapis.
    /// Kartice javljanja u četu ostaju, samo bez veze ka poslu.
    /// </summary>
    internal async Task<bool> TrajnoObrisiAsync(int jobId)
    {
        if (await db.Reports.AnyAsync(r => r.JobId == jobId))
        {
            await db.Jobs.Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.PurgeAt, (DateTime?)null));
            return false;
        }

        // NoAction veza (vidi AppDbContext) — referenca se skida ručno.
        await db.Messages
            .Where(m => m.JobApplicationId != null && m.JobApplication!.JobId == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.JobApplicationId, (int?)null));

        slike.ObrisiSve(jobId);

        // Slike, javljanja i isticanja odlaze kaskadom.
        var obrisano = await db.Jobs.Where(j => j.Id == jobId).ExecuteDeleteAsync();
        if (obrisano > 0)
            logger.LogInformation("Posao {JobId} trajno obrisan.", jobId);
        return obrisano > 0;
    }

    // ── PRIKAZ ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Detalj posla za gledaoca. Aktivan vidi svako (osim blokiranih);
    /// dogovoren — samo autor i izabrani; istekao — samo autor; izvršen i
    /// obrisan — niko (null → „Ovaj posao više nije dostupan").
    /// </summary>
    public async Task<JobDto?> GetAsync(int jobId, string? viewerId)
    {
        var job = await SaPodacima(db.Jobs.AsNoTracking())
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job is null) return null;

        var sada  = DateTime.UtcNow;
        var autor = viewerId is not null && job.AuthorId == viewerId;
        var izabrani = job.AssignedApplicationId is int aid &&
                       job.Applications.Any(a => a.Id == aid && a.ProviderUserId == viewerId);

        if (!autor)
        {
            if (job.ModerationState == ModerationState.Removed || !job.Author.IsActive) return null;
            if (viewerId is not null && await JeBlokiranoAsync(viewerId, job.AuthorId)) return null;

            var vidljiv = job.Status switch
            {
                JobStatus.Open     => job.ExpiresAt > sada,
                JobStatus.Assigned => izabrani,
                _                  => false
            };
            if (!vidljiv) return null;
        }
        else if (job.Status is JobStatus.Completed or JobStatus.Deleted)
        {
            return null;
        }

        var dto = JobMapa.UDto(job, viewerId, pravila, sada);
        if ((autor || izabrani) && job.Status == JobStatus.Assigned)
            dto.Assigned = await DogovorAsync(job, viewerId!);

        return dto;
    }

    /// <summary>Dogovor (izvršilac, od kada, kada može „izvršen") za autora i izabranog.</summary>
    private async Task<AssignmentDto?> DogovorAsync(Job job, string viewerId)
    {
        var prijava = job.Applications.FirstOrDefault(a => a.Id == job.AssignedApplicationId);
        if (prijava is null || job.AssignedAt is not DateTime od) return null;

        var izvrsilac = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == prijava.ProviderUserId);
        var profil    = await db.ProviderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == prijava.ProviderUserId);
        var razgovor  = await RazgovorIdAsync(job.AuthorId, prijava.ProviderUserId);

        return new AssignmentDto
        {
            ApplicationId  = prijava.Id,
            Worker         = JobMapa.Osoba(izvrsilac, kaoNarucilac: false, profil),
            OfferAmount    = prijava.OfferAmount,
            AssignedAt     = od,
            CompletableAt  = od.AddDays(pravila.CekanjeDoIzvrsenja),
            ConversationId = razgovor
        };
    }

    // ── PRETRAGA ───────────────────────────────────────────────────────────
    /// <summary>
    /// Aktivni poslovi — isti parametri i isto preklapanje teksta kao pretraga
    /// oglasa (upit, grad, kategorija). Dva sloja: svi tokeni u naslovu ili
    /// kategoriji, pa — ako je premalo — i u opisu.
    ///
    /// Istaknuti stoje u POSEBNOJ SEKCIJI: Boosted=true vraća samo njih
    /// (sekcija), Boosted=false samo ostale (lista) — posao se ne pojavljuje
    /// dvaput.
    /// </summary>
    public async Task<PagedJobs> SearchAsync(JobQueryParams p)
    {
        p.PageSize = Math.Clamp(p.PageSize, 1, 50);
        p.Page     = Math.Max(p.Page, 1);
        var sada   = DateTime.UtcNow;

        var upit = SaPodacima(db.Jobs.AsNoTracking())
            .Where(j => j.Status == JobStatus.Open
                     && j.ExpiresAt > sada
                     && j.ModerationState != ModerationState.Removed
                     && j.Author.IsActive);

        if (!string.IsNullOrEmpty(p.ViewerUserId))
        {
            var v = p.ViewerUserId;
            upit = upit.Where(j => !db.UserBlocks.Any(ub =>
                (ub.BlockerId == v && ub.BlockedId == j.AuthorId) ||
                (ub.BlockedId == v && ub.BlockerId == j.AuthorId)));
        }

        if (p.Boosted == true)
            upit = upit.Where(j => j.IsBoosted && j.BoostExpiresAt > sada);
        else if (p.Boosted == false)
            upit = upit.Where(j => !(j.IsBoosted && j.BoostExpiresAt > sada));

        if (!string.IsNullOrWhiteSpace(p.CategorySlug))
        {
            var slug = p.CategorySlug.Trim().ToLowerInvariant();
            var ids  = await db.Categories
                .Where(c => c.Slug == slug || (c.Parent != null && c.Parent.Slug == slug))
                .Select(c => c.Id)
                .ToListAsync();
            if (ids.Count > 0) upit = upit.Where(j => ids.Contains(j.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(p.City))
        {
            var grad = SearchNormalizer.Fold(p.City);
            if (grad.Length > 0)
            {
                var obrazac = SearchNormalizer.EscapeLike(grad) + "%";
                upit = upit.Where(j => EF.Functions.Like(j.SearchLocation, obrazac, LikeEscape));
            }
        }

        var q = SearchQuery.Parse(p.Q);
        if (!q.IsEmpty)
        {
            var kategorije = await categoryIndex.MatchPerTokenAsync(q);
            var uzi = SviTokeni(upit, q, kategorije, saOpisom: false);
            upit = await uzi.CountAsync() >= MinRezultata
                ? uzi
                : SviTokeni(upit, q, kategorije, saOpisom: true);
        }

        var ukupno = await upit.CountAsync();
        var poredak = p.Boosted == true
            ? upit.OrderByDescending(j => j.BoostScore).ThenByDescending(j => j.CreatedAt)
            : upit.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id);

        var stranica = await poredak.Skip((p.Page - 1) * p.PageSize).Take(p.PageSize).ToListAsync();

        return new PagedJobs
        {
            Items    = stranica.Select(j => JobMapa.UDto(j, p.ViewerUserId, pravila, sada)).ToList(),
            Total    = ukupno,
            Page     = p.Page,
            PageSize = p.PageSize
        };
    }

    private static IQueryable<Job> SviTokeni(
        IQueryable<Job> izvor, SearchQuery q,
        IReadOnlyDictionary<string, HashSet<int>> kategorije, bool saOpisom)
    {
        foreach (var token in q.Tokens)
        {
            var uslovi = new List<Expression<Func<Job, bool>>>();
            foreach (var varijanta in token.Variants)
            {
                var obrazac = "%" + SearchNormalizer.EscapeLike(varijanta) + "%";
                uslovi.Add(j => EF.Functions.Like(j.SearchTitle, obrazac, LikeEscape));
                uslovi.Add(j => EF.Functions.Like(j.SearchLocation, obrazac, LikeEscape));
                if (saOpisom)
                    uslovi.Add(j => EF.Functions.Like(j.SearchBody, obrazac, LikeEscape));
            }

            if (kategorije.TryGetValue(token.Value, out var ids))
            {
                var lista = ids.ToList();
                uslovi.Add(j => lista.Contains(j.CategoryId));
            }

            var spojeno = PredicateBuilder.OrAll(uslovi);
            if (spojeno is not null) izvor = izvor.Where(spojeno);
        }

        return izvor;
    }

    // ── MOJI POSLOVI ───────────────────────────────────────────────────────
    /// <summary>
    /// „Objavio sam": aktivni, dogovoreni, istekli + traka „Za tebe" sa onim
    /// što traži pažnju (najhitnije prvo).
    /// </summary>
    public async Task<MyJobsDto> MineAsync(string authorId)
    {
        var sada = DateTime.UtcNow;
        var poslovi = await SaPodacima(db.Jobs.AsNoTracking())
            .Where(j => j.AuthorId == authorId
                     && (j.Status == JobStatus.Open || j.Status == JobStatus.Assigned || j.Status == JobStatus.Expired))
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var rez = new MyJobsDto();
        var istice = new List<ForYouItemDto>();
        var nova   = new List<ForYouItemDto>();
        var spremno = new List<ForYouItemDto>();

        foreach (var j in poslovi)
        {
            var dto = JobMapa.UDto(j, authorId, pravila, sada);

            if (j.ModerationState == ModerationState.Removed) { rez.Removed.Add(dto); continue; }

            switch (j.Status)
            {
                case JobStatus.Open:
                    rez.Active.Add(dto);
                    if (j.ExpiresAt - sada <= TimeSpan.FromDays(1))
                        istice.Add(new ForYouItemDto { Kind = "expiring", JobId = j.Id, JobTitle = j.Title, Subtitle = "Ističe sutra" });
                    if (dto.NewApplicationsCount > 0)
                        nova.Add(new ForYouItemDto
                        {
                            Kind = "new-applications", JobId = j.Id, JobTitle = j.Title,
                            Subtitle = dto.NewApplicationsCount == 1 ? "1 novo javljanje" : $"{dto.NewApplicationsCount} nova javljanja"
                        });
                    break;

                case JobStatus.Assigned:
                    dto.Assigned = await DogovorAsync(j, authorId);
                    rez.Assigned.Add(dto);
                    if (dto.Assigned is { } d && d.CompletableAt <= sada)
                        spremno.Add(new ForYouItemDto { Kind = "completable", JobId = j.Id, JobTitle = j.Title, Subtitle = "Možeš da označiš izvršen" });
                    break;

                case JobStatus.Expired:
                    rez.Expired.Add(dto);
                    break;
            }
        }

        rez.ForYou = [.. istice, .. nova, .. spremno];
        return rez;
    }

    /// <summary>„Javio sam se" — poslovi na koje se uslugodavac javio, sa stanjem javljanja.</summary>
    public async Task<List<AppliedJobDto>> AppliedAsync(string providerId)
    {
        var sada = DateTime.UtcNow;
        var poslovi = await SaPodacima(db.Jobs.AsNoTracking())
            .Where(j => j.Applications.Any(a => a.ProviderUserId == providerId && a.Status != JobApplicationStatus.Withdrawn)
                     && (j.Status == JobStatus.Open || j.Status == JobStatus.Assigned)
                     && j.ModerationState != ModerationState.Removed)
            .ToListAsync();

        var rez = new List<AppliedJobDto>();
        foreach (var j in poslovi)
        {
            var moja = j.Applications.First(a => a.ProviderUserId == providerId);

            // Dogovoren sa drugim i ja sam odustao — više nije moj posao.
            if (moja.Status == JobApplicationStatus.Quit && j.Status == JobStatus.Assigned) continue;

            rez.Add(new AppliedJobDto
            {
                Job            = JobMapa.UDto(j, providerId, pravila, sada),
                Application    = JobMapa.MojaPrijava(moja, j),
                ConversationId = await RazgovorIdAsync(j.AuthorId, providerId)
            });
        }

        // Izabran si → na vrh, pa po vremenu javljanja.
        return rez
            .OrderByDescending(r => r.Application.Status == JobApplicationStatus.Chosen)
            .ThenByDescending(r => r.Application.CreatedAt)
            .ToList();
    }

    // ── POMOĆNO ────────────────────────────────────────────────────────────

    /// <summary>Učitava sve što <see cref="JobMapa.UDto"/> čita.</summary>
    internal static IQueryable<Job> SaPodacima(IQueryable<Job> upit) => upit
        .Include(j => j.Author)
        .Include(j => j.Category)
        .Include(j => j.Images)
        .Include(j => j.Applications)
        .AsSplitQuery();

    internal async Task<int?> RazgovorIdAsync(string a, string b)
    {
        var (u1, u2) = string.Compare(a, b, StringComparison.Ordinal) < 0 ? (a, b) : (b, a);
        return await db.Conversations
            .Where(c => c.User1Id == u1 && c.User2Id == u2)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
    }

    private Task<bool> JeBlokiranoAsync(string a, string b) =>
        db.UserBlocks.AnyAsync(ub =>
            (ub.BlockerId == a && ub.BlockedId == b) || (ub.BlockerId == b && ub.BlockedId == a));

    private async Task<string?> ProveriKategorijuIGradAsync(int categoryId, string grad)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId))
            return "Izaberi kategoriju.";
        if (NormalizujGrad(grad).Length == 0)
            return "Izaberi grad sa liste.";
        return null;
    }

    /// <summary>
    /// Grad sa zvanične liste opština, u njenom pisanju („cacak" → „Čačak").
    /// Prazno ako nije na listi — posao bez pravog grada se ne bi našao filterom.
    /// </summary>
    private static string NormalizujGrad(string unos)
    {
        var f = SearchNormalizer.Fold(unos);
        return SerbianMunicipalities.All.FirstOrDefault(g => SearchNormalizer.Fold(g) == f) ?? string.Empty;
    }
}
