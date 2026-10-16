using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services.Jobs;

/// <summary>
/// Javljanja i dogovor: javi se, povuci, lista javljanja, izbor izvršioca,
/// otkazivanje, odustajanje i „Posao je izvršen" (tokeni).
///
/// SVAKI PRELAZ STANJA IDE USLOVNIM UPDATE-om (WHERE Status = očekivano): dva
/// brza dodira ili dva uređaja ne smeju da naprave dupli prelaz — a kod
/// „izvršen" bi to značilo duplu isplatu tokena.
/// </summary>
public sealed class JobApplicationService(
    AppDbContext                    db,
    JobPravila                      pravila,
    JobSlike                        slike,
    ChatSender                      chat,
    NotificationService             notifications,
    BlockService                    blokade,
    ILogger<JobApplicationService>  logger)
{
    // ── JAVI SE ────────────────────────────────────────────────────────────
    /// <summary>
    /// Uslugodavac se javlja. Odmah se otvara razgovor sa autorom i u njemu
    /// stoji kartica javljanja (odluka 15.10.): autor vidi „Marko se javio na
    /// posao …", uslugodavac „Javio si se na posao … kod Milice".
    ///
    /// Ponovno javljanje posle povlačenja ili otkazivanja menja ISTI red
    /// (UNIQUE posao + uslugodavac); posle otkazivanja autor ga vidi na vrhu
    /// liste kao „Ponovo se javio".
    /// </summary>
    public async Task<(MyApplicationDto? Application, int? ConversationId, string? Error)> ApplyAsync(
        int jobId, string providerId, ApplyToJobDto dto)
    {
        var uslugodavac = await db.Users.FirstOrDefaultAsync(u => u.Id == providerId);
        if (uslugodavac is null || !uslugodavac.IsActive)
            return (null, null, "Nalog nije pronađen.");

        if (!await db.ProviderProfiles.AnyAsync(p => p.UserId == providerId))
            return (null, null, "Na poslove se javljaju uslugodavci. Postani uslugodavac da se javiš.");

        var job = await db.Jobs.Include(j => j.Author)
            .FirstOrDefaultAsync(j => j.Id == jobId);
        var sada = DateTime.UtcNow;

        if (job is null || job.Status != JobStatus.Open || job.ExpiresAt <= sada
            || job.ModerationState == ModerationState.Removed || !job.Author.IsActive)
            return (null, null, "Ovaj posao više nije dostupan.");

        if (job.AuthorId == providerId)
            return (null, null, "Ne možeš se javiti na svoj posao.");

        if (await blokade.JeBlokiranoAsync(providerId, job.AuthorId))
            return (null, null, "Javljanje na ovaj posao nije moguće.");

        var poruka = dto.Message.Trim();
        if (poruka.Length < 2)
            return (null, null, "Napiši kratku poruku — autor bira po njoj.");

        var prijava = await db.JobApplications
            .FirstOrDefaultAsync(a => a.JobId == jobId && a.ProviderUserId == providerId);

        if (prijava is { Status: JobApplicationStatus.Sent or JobApplicationStatus.Chosen })
            return (null, null, "Već si se javio na ovaj posao.");

        if (prijava is null)
        {
            prijava = new JobApplication
            {
                JobId          = jobId,
                ProviderUserId = providerId,
                CreatedAt      = sada
            };
            db.JobApplications.Add(prijava);
        }
        else
        {
            // Otkazan ili odustao, pa se ponovo javlja — autor to treba da vidi.
            prijava.IsReapplied  = prijava.Status is JobApplicationStatus.Cancelled or JobApplicationStatus.Quit;
            prijava.CancelReason = null;
            prijava.CreatedAt    = sada;
        }

        prijava.Message     = poruka;
        prijava.OfferAmount = dto.OfferAmount;
        prijava.Status      = JobApplicationStatus.Sent;
        prijava.UpdatedAt   = sada;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Dva istovremena javljanja istog uslugodavca — UNIQUE je pustio jedno.
            return (null, null, "Već si se javio na ovaj posao.");
        }

        // Razgovor + kartica javljanja. Kartica ide BEZ opšteg „Nova poruka" —
        // autor dobija preciznije obaveštenje ispod.
        prijava.Job = job;
        var razgovor = await chat.RazgovorAsync(providerId, job.AuthorId);
        await chat.PosaljiAsync(razgovor, uslugodavac, poruka, MessageKind.JobApplication, prijava, obavesti: false);

        await notifications.SendAsync(job.AuthorId, NotificationKind.JobApplicationReceived,
            "Novo javljanje na posao",
            $"{uslugodavac.FullName} se javio na „{job.Title}“.",
            job.Id);

        logger.LogInformation("Javljanje {AppId} na posao {JobId} (uslugodavac {ProviderId}).",
            prijava.Id, jobId, providerId);

        return (JobMapa.MojaPrijava(prijava, job), razgovor.Id, null);
    }

    // ── POVUCI ─────────────────────────────────────────────────────────────
    public async Task<(bool Ok, string? Error)> WithdrawAsync(int jobId, string providerId)
    {
        var izmenjeno = await db.JobApplications
            .Where(a => a.JobId == jobId && a.ProviderUserId == providerId && a.Status == JobApplicationStatus.Sent)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, JobApplicationStatus.Withdrawn)
                .SetProperty(a => a.UpdatedAt, DateTime.UtcNow));

        return izmenjeno == 1
            ? (true, null)
            : (false, "Javljanje ne može da se povuče — možda si već izabran.");
    }

    /// <summary>
    /// Brisanje naloga uslugodavca: poslata javljanja se povlače, a posao na
    /// kom je izabran vraća se autoru kao da je odustao (autor dobija
    /// obaveštenje, ostali javljeni „ponovo je aktivan").
    /// </summary>
    public async Task PovuciSveAsync(string providerId)
    {
        var dogovoreni = await db.JobApplications
            .Where(a => a.ProviderUserId == providerId && a.Status == JobApplicationStatus.Chosen)
            .Select(a => a.JobId)
            .ToListAsync();

        foreach (var jobId in dogovoreni)
            await QuitAsync(jobId, providerId);

        await db.JobApplications
            .Where(a => a.ProviderUserId == providerId && a.Status == JobApplicationStatus.Sent)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, JobApplicationStatus.Withdrawn)
                .SetProperty(a => a.UpdatedAt, DateTime.UtcNow));
    }

    // ── LISTA JAVLJANJA (autor) ────────────────────────────────────────────
    /// <summary>
    /// Javljanja kako ih vidi autor. Otvaranje liste beleži da su viđena
    /// (nestaje „2 nova"). Redosled: „Ponovo se javio" na vrhu, pa po izboru
    /// sortiranja; otkazani i odustali na dnu.
    /// </summary>
    public async Task<(List<JobApplicationDto>? Items, string? Error)> ListAsync(
        int jobId, string authorId, string? sort)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);
        if (job is null || job.Status is JobStatus.Completed or JobStatus.Deleted)
            return (null, "Posao nije pronađen.");

        var prijave = await db.JobApplications.AsNoTracking()
            .Include(a => a.Provider)
            .Where(a => a.JobId == jobId && a.Status != JobApplicationStatus.Withdrawn)
            .ToListAsync();

        var korisnici = prijave.Select(a => a.ProviderUserId).ToList();
        var profili = await db.ProviderProfiles.AsNoTracking()
            .Where(p => korisnici.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId);

        var razgovori = await RazgovoriSaAutoromAsync(authorId, korisnici);

        var lista = prijave.Select(a => new JobApplicationDto
        {
            Id             = a.Id,
            Provider       = JobMapa.Osoba(a.Provider, kaoNarucilac: false, profili.GetValueOrDefault(a.ProviderUserId)),
            Message        = a.Message,
            OfferAmount    = a.OfferAmount,
            Status         = a.Status,
            IsReapplied    = a.IsReapplied && a.Status == JobApplicationStatus.Sent,
            CreatedAt      = a.CreatedAt,
            ConversationId = razgovori.GetValueOrDefault(a.ProviderUserId)
        });

        // Aktivna javljanja prva; među njima „Ponovo se javio" na vrhu.
        var aktivna = lista.Where(a => a.Status is JobApplicationStatus.Sent or JobApplicationStatus.Chosen);
        aktivna = (sort ?? "newest") switch
        {
            "rating" => aktivna.OrderByDescending(a => a.IsReapplied).ThenByDescending(a => a.Provider.Rating).ThenByDescending(a => a.Provider.RatingCount),
            "offer"  => aktivna.OrderByDescending(a => a.IsReapplied).ThenBy(a => a.OfferAmount ?? decimal.MaxValue),
            _        => aktivna.OrderByDescending(a => a.IsReapplied).ThenByDescending(a => a.CreatedAt)
        };
        var ostala = lista.Where(a => a.Status is JobApplicationStatus.Cancelled or JobApplicationStatus.Quit)
            .OrderByDescending(a => a.CreatedAt);

        job.ApplicationsSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return ([.. aktivna, .. ostala], null);
    }

    // ── IZBOR ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Autor bira izvršioca. Posao se skida sa pretrage; rok i isticanje
    /// STOJE dok traje dogovor (odluka 11) — pamti se koliko je ostalo.
    /// </summary>
    public async Task<(int? ConversationId, string? Error)> AssignAsync(int jobId, string authorId, int applicationId)
    {
        var job = await db.Jobs.Include(j => j.Applications)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);
        if (job is null || job.Status != JobStatus.Open)
            return (null, "Izbor je moguć samo dok je posao aktivan.");

        var prijava = job.Applications.FirstOrDefault(a => a.Id == applicationId);
        if (prijava is null || prijava.Status != JobApplicationStatus.Sent)
            return (null, "Ovo javljanje više ne važi.");

        if (await blokade.JeBlokiranoAsync(authorId, prijava.ProviderUserId))
            return (null, "Izbor ovog uslugodavca nije moguć.");

        var sada     = DateTime.UtcNow;
        TimeSpan? ostatak = job.ExpiresAt > sada ? job.ExpiresAt - sada : TimeSpan.Zero;
        var istaknut = job.IsBoosted && job.BoostExpiresAt > sada ? job.BoostExpiresAt!.Value - sada : (TimeSpan?)null;

        var prelaz = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Assigned)
                .SetProperty(j => j.AssignedApplicationId, applicationId)
                .SetProperty(j => j.AssignedAt, sada)
                .SetProperty(j => j.RemainingOnAssign, ostatak)
                .SetProperty(j => j.BoostRemainingOnAssign, istaknut)
                .SetProperty(j => j.IsBoosted, false)
                .SetProperty(j => j.CompletableNotifiedAt, (DateTime?)null)
                .SetProperty(j => j.UpdatedAt, sada));
        if (prelaz != 1)
            return (null, "Posao je u međuvremenu promenjen. Osveži ekran.");

        await db.JobApplications.Where(a => a.Id == applicationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, JobApplicationStatus.Chosen)
                .SetProperty(a => a.UpdatedAt, sada));

        var razgovor = await chat.RazgovorAsync(authorId, prijava.ProviderUserId);
        var autor    = await db.Users.AsNoTracking().FirstAsync(u => u.Id == authorId);

        await notifications.SendAsync(prijava.ProviderUserId, NotificationKind.JobChosen,
            "Izabran si za posao",
            $"Izabran si za „{job.Title}“. Dogovori termin sa {autor.FullName} u poruci.",
            job.Id);

        foreach (var drugi in job.Applications.Where(a => a.Id != applicationId && a.Status == JobApplicationStatus.Sent))
            await notifications.SendAsync(drugi.ProviderUserId, NotificationKind.JobNotChosen,
                "Autor je izabrao drugog",
                $"Za „{job.Title}“ je izabran drugi uslugodavac. Tvoje javljanje važi ako se posao vrati.",
                job.Id);

        logger.LogInformation("Posao {JobId} dogovoren sa javljanjem {AppId}.", jobId, applicationId);
        return (razgovor.Id, null);
    }

    // ── OTKAZIVANJE (autor) ────────────────────────────────────────────────
    public async Task<(bool Ok, string? Error)> UnassignAsync(int jobId, string authorId, string? razlog)
    {
        var job = await db.Jobs.Include(j => j.Applications)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);
        if (job is null || job.Status != JobStatus.Assigned)
            return (false, "Posao nije dogovoren.");
        if (job.CancelCount >= pravila.NajviseOtkazivanja)
            return (false, $"Otkazao si izvršioca {pravila.NajviseOtkazivanja} puta. Na ovom poslu više ne možeš da biraš — obriši ga i objavi novi.");

        var izabrana = job.Applications.FirstOrDefault(a => a.Id == job.AssignedApplicationId);

        if (!await VratiUAktivanAsync(job, povecajOtkazivanja: true))
            return (false, "Posao je u međuvremenu promenjen. Osveži ekran.");

        if (izabrana is not null)
        {
            await db.JobApplications.Where(a => a.Id == izabrana.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, JobApplicationStatus.Cancelled)
                    .SetProperty(a => a.CancelReason, string.IsNullOrWhiteSpace(razlog) ? null : razlog.Trim())
                    .SetProperty(a => a.UpdatedAt, DateTime.UtcNow));

            await notifications.SendAsync(izabrana.ProviderUserId, NotificationKind.JobAssignmentCancelled,
                "Dogovor je otkazan",
                $"Autor je otkazao dogovor za „{job.Title}“. Možeš ponovo da se javiš.",
                job.Id);
        }

        await JaviOstalimaDaJeOtvorenAsync(job, izabrana?.Id);
        return (true, null);
    }

    // ── ODUSTAJANJE (izvršilac) ────────────────────────────────────────────
    public async Task<(bool Ok, string? Error)> QuitAsync(int jobId, string providerId)
    {
        var job = await db.Jobs.Include(j => j.Applications)
            .FirstOrDefaultAsync(j => j.Id == jobId);
        var izabrana = job?.Applications.FirstOrDefault(a => a.Id == job.AssignedApplicationId);

        if (job is null || job.Status != JobStatus.Assigned || izabrana?.ProviderUserId != providerId)
            return (false, "Nisi izvršilac ovog posla.");

        // Odustajanje se NE broji u autorova otkazivanja — nije njegova odluka.
        if (!await VratiUAktivanAsync(job, povecajOtkazivanja: false))
            return (false, "Posao je u međuvremenu promenjen. Osveži ekran.");

        await db.JobApplications.Where(a => a.Id == izabrana.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, JobApplicationStatus.Quit)
                .SetProperty(a => a.UpdatedAt, DateTime.UtcNow));

        var izvrsilac = await db.Users.AsNoTracking().FirstAsync(u => u.Id == providerId);
        await notifications.SendAsync(job.AuthorId, NotificationKind.JobWorkerQuit,
            "Izvršilac je odustao",
            $"{izvrsilac.FullName} je odustao — „{job.Title}“ je ponovo aktivan. Ostala javljanja važe.",
            job.Id);

        await JaviOstalimaDaJeOtvorenAsync(job, izabrana.Id);
        return (true, null);
    }

    // ── IZVRŠEN ────────────────────────────────────────────────────────────
    /// <summary>
    /// Autor potvrđuje da je posao urađen — tek posle čekanja od izbora (brana
    /// od lažnih poslova). Tokeni: autor 3, izvršilac 5, TAČNO JEDNOM. Posao
    /// zatim nestaje svuda; red ostaje 14 dana zbog ocena.
    /// </summary>
    public async Task<(decimal? Nagrada, string? Error)> CompleteAsync(int jobId, string authorId)
    {
        var job = await db.Jobs.Include(j => j.Applications).Include(j => j.Images)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.AuthorId == authorId);
        if (job is null || job.Status != JobStatus.Assigned || job.AssignedAt is not DateTime od)
            return (null, "Posao nije dogovoren.");

        var sada = DateTime.UtcNow;
        var moze = od.AddDays(pravila.CekanjeDoIzvrsenja);
        if (moze > sada)
            return (null, $"„Posao je izvršen“ možeš da označiš od {moze:dd.MM. u HH:mm} — čeka se {pravila.CekanjeDoIzvrsenja} dana od izbora.");

        var izabrana = job.Applications.First(a => a.Id == job.AssignedApplicationId);

        await using var trx = await db.Database.BeginTransactionAsync();

        // Uslov u UPDATE-u: drugi istovremeni poziv nađe Status != Assigned i
        // ne isplaćuje ništa (isti obrazac kao isplata referral nagrade).
        var prelaz = await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Assigned)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Completed)
                .SetProperty(j => j.CompletedAt, sada)
                .SetProperty(j => j.Description, string.Empty)
                .SetProperty(j => j.PurgeAt, sada.AddDays(pravila.RokZaOcene))
                .SetProperty(j => j.UpdatedAt, sada));
        if (prelaz != 1)
        {
            await trx.RollbackAsync();
            return (null, "Posao je već označen kao izvršen.");
        }

        await IsplatiAsync(authorId, pravila.NagradaAutoru, jobId, $"Izvršen posao: {job.Title}", sada);
        await IsplatiAsync(izabrana.ProviderUserId, pravila.NagradaIzvrsiocu, jobId, $"Izvršen posao: {job.Title}", sada);
        await trx.CommitAsync();

        // Slike odlaze odmah — opis je već obrisan u istom UPDATE-u.
        slike.ObrisiSve(job.Id);
        db.JobImages.RemoveRange(job.Images);
        await db.SaveChangesAsync();

        var autor     = await db.Users.AsNoTracking().FirstAsync(u => u.Id == authorId);
        var izvrsilac = await db.Users.AsNoTracking().FirstAsync(u => u.Id == izabrana.ProviderUserId);

        await notifications.SendAsync(izabrana.ProviderUserId, NotificationKind.JobCompleted,
            $"+{pravila.NagradaIzvrsiocu:0.##} tokena",
            $"„{job.Title}“ je izvršen. Oceni {autor.FullName} kao naručioca.",
            job.Id);
        await notifications.SendAsync(authorId, NotificationKind.JobCompleted,
            $"+{pravila.NagradaAutoru:0.##} tokena",
            $"„{job.Title}“ je izvršen. Oceni {izvrsilac.FullName}.",
            job.Id);

        logger.LogInformation("Posao {JobId} izvršen: autor +{A}, izvršilac +{I}.",
            jobId, pravila.NagradaAutoru, pravila.NagradaIzvrsiocu);
        return (pravila.NagradaAutoru, null);
    }

    // ── POMOĆNO ────────────────────────────────────────────────────────────

    /// <summary>
    /// Dogovoren → aktivan (otkazivanje ili odustajanje). Rok i isticanje se
    /// vraćaju sa vremenom koje su imali pri izboru; rok najmanje 3 dana.
    /// </summary>
    private async Task<bool> VratiUAktivanAsync(Job job, bool povecajOtkazivanja)
    {
        var sada   = DateTime.UtcNow;
        var ostalo = job.RemainingOnAssign ?? TimeSpan.Zero;
        if (ostalo < pravila.NajmanjeRokaPosleOtkazivanja) ostalo = pravila.NajmanjeRokaPosleOtkazivanja;

        var isticanje = job.BoostRemainingOnAssign is { } b && b > TimeSpan.Zero ? b : (TimeSpan?)null;
        var istaknut  = isticanje is not null;
        var istice    = isticanje is { } t ? sada + t : job.BoostExpiresAt;
        var noviRok   = sada + ostalo;
        var korak     = povecajOtkazivanja ? 1 : 0;

        var prelaz = await db.Jobs
            .Where(j => j.Id == job.Id && j.Status == JobStatus.Assigned)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Open)
                .SetProperty(j => j.AssignedApplicationId, (int?)null)
                .SetProperty(j => j.AssignedAt, (DateTime?)null)
                .SetProperty(j => j.RemainingOnAssign, (TimeSpan?)null)
                .SetProperty(j => j.ExpiresAt, noviRok)
                .SetProperty(j => j.ExpiryNotifiedAt, (DateTime?)null)
                .SetProperty(j => j.IsBoosted, istaknut)
                .SetProperty(j => j.BoostExpiresAt, istice)
                .SetProperty(j => j.BoostRemainingOnAssign, (TimeSpan?)null)
                .SetProperty(j => j.CancelCount, j => j.CancelCount + korak)
                .SetProperty(j => j.UpdatedAt, sada));

        return prelaz == 1;
    }

    private async Task JaviOstalimaDaJeOtvorenAsync(Job job, int? osimId)
    {
        foreach (var a in job.Applications.Where(a => a.Id != osimId && a.Status == JobApplicationStatus.Sent))
            await notifications.SendAsync(a.ProviderUserId, NotificationKind.JobReopened,
                "Posao je ponovo otvoren",
                $"„{job.Title}“ je ponovo otvoren — tvoje javljanje i dalje važi.",
                job.Id);
    }

    /// <summary>Tokeni jednom SQL naredbom (balans = balans + x) + zapis u ledger.</summary>
    private async Task IsplatiAsync(string userId, decimal iznos, int jobId, string opis, DateTime sada)
    {
        await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenBalance, u => u.TokenBalance + iznos));

        var balans = await db.Users.Where(u => u.Id == userId).Select(u => u.TokenBalance).FirstAsync();
        db.UskladiBalans(userId, balans);

        db.TokenTransactions.Add(new TokenTransaction
        {
            UserId       = userId,
            Amount       = iznos,
            Kind         = TokenKind.JobReward,
            ReferenceId  = jobId,
            Description  = opis.Length > 480 ? opis[..480] : opis,
            BalanceAfter = balans,
            CreatedAt    = sada
        });
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<string, int>> RazgovoriSaAutoromAsync(string authorId, List<string> drugi)
    {
        var redovi = await db.Conversations.AsNoTracking()
            .Where(c => (c.User1Id == authorId && drugi.Contains(c.User2Id))
                     || (c.User2Id == authorId && drugi.Contains(c.User1Id)))
            .Select(c => new { c.Id, Drugi = c.User1Id == authorId ? c.User2Id : c.User1Id })
            .ToListAsync();

        return redovi.GroupBy(r => r.Drugi).ToDictionary(g => g.Key, g => g.First().Id);
    }
}
