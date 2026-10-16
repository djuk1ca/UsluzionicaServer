using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.Infrastructure.Demo;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Poslovi — ceo životni ciklus iz plana (Obsidian „Plan — Poslovi i ocene
/// korisnika (v2)" §10). U testovima je čekanje do „izvršen" 3 dana
/// (Booking:ExecuteAfterDays u UsluzionicaWebFactory).
/// </summary>
public class JobsTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // ── Priprema ───────────────────────────────────────────────────────────

    private async Task<(string AutorId, string MajstorId, int PosaoId)> PosaoSaMajstoromAsync()
    {
        var autor      = await Data.CreateConfirmedUserAsync("autor@test.rs", fullName: "Milica Autor");
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs", fullName: "Petar Majstor");
        var posaoId    = await ObjaviAsync(autor.Id);
        return (autor.Id, majstor.Id, posaoId);
    }

    private async Task<int> ObjaviAsync(string autorId, string naslov = "Treba okačiti police")
    {
        var (job, greska) = await Objava(autorId, naslov);
        job.Should().NotBeNull(greska);
        return job!.Id;
    }

    private Task<(JobDto?, string?)> Objava(string autorId, string naslov = "Treba okačiti police") =>
        WithService<JobService, (JobDto?, string?)>(svc => svc.CreateAsync(autorId, new CreateJobDto
        {
            Title       = naslov,
            CategoryId  = TestData.SeededCategoryId,
            Location    = TestData.ValidCity,
            Description = "Tri police u dnevnoj sobi, zid je od cigle.",
            PayAmount   = 3000m
        }));

    private Task<(MyApplicationDto? Application, int? ConversationId, string? Error)> JaviSeAsync(
        int posaoId, string majstorId, decimal? ponuda = null) =>
        WithService<JobApplicationService, (MyApplicationDto?, int?, string?)>(svc =>
            svc.ApplyAsync(posaoId, majstorId, new ApplyToJobDto { Message = "Mogu sutra posle podne.", OfferAmount = ponuda }));

    private async Task<int> IzaberiAsync(int posaoId, string autorId, int prijavaId)
    {
        var (razgovor, greska) = await WithService<JobApplicationService, (int?, string?)>(svc =>
            svc.AssignAsync(posaoId, autorId, prijavaId));
        razgovor.Should().NotBeNull(greska);
        return razgovor!.Value;
    }

    private Task<Job> PosaoAsync(int id) =>
        Query(db => db.Jobs.Include(j => j.Applications).SingleAsync(j => j.Id == id));

    /// <summary>Pomera izbor u prošlost — da „izvršen" bude dozvoljen.</summary>
    private Task PomeriIzborAsync(int posaoId, int dana) =>
        Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.AssignedAt, DateTime.UtcNow.AddDays(-dana))));

    private Task<int> ObavestenjaAsync(string userId, NotificationKind kind) =>
        Query(db => db.Notifications.CountAsync(n => n.UserId == userId && n.Kind == kind));

    private Task<PagedJobs> PretragaAsync(string gledalacId, bool? istaknuti = null) =>
        WithService<JobService, PagedJobs>(svc => svc.SearchAsync(new JobQueryParams
        {
            ViewerUserId = gledalacId,
            Boosted      = istaknuti
        }));

    private static IFormFile Slika()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46,
                       0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                       0xFF, 0xD9];
        var ms = new MemoryStream(jpeg);
        return new FormFile(ms, 0, ms.Length, "file", "posao.jpg")
        {
            Headers     = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    // ── Objava ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Objava_NepotvrdjenMejl_Odbijena()
    {
        var korisnik = await Data.CreateUnconfirmedUserAsync("nepotvrdjen@test.rs");

        var (job, greska) = await Objava(korisnik.Id);

        job.Should().BeNull();
        greska.Should().Contain("mejl");
    }

    [Fact]
    public async Task Objava_SestiAktivan_Odbijen()
    {
        var autor = await Data.CreateConfirmedUserAsync("autor@test.rs");
        for (var i = 0; i < 5; i++)
            await ObjaviAsync(autor.Id, $"Posao broj {i + 1}");

        var (job, greska) = await Objava(autor.Id, "Posao broj 6");

        job.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task TrecaSlika_Odbijena()
    {
        var autor   = await Data.CreateConfirmedUserAsync("autor@test.rs");
        var posaoId = await ObjaviAsync(autor.Id);

        for (var i = 0; i < 2; i++)
        {
            var (slika, g) = await WithService<JobService, (JobImageDto?, string?)>(svc =>
                svc.UploadImageAsync(posaoId, autor.Id, Slika()));
            slika.Should().NotBeNull(g);
        }

        var (treca, greska) = await WithService<JobService, (JobImageDto?, string?)>(svc =>
            svc.UploadImageAsync(posaoId, autor.Id, Slika()));

        treca.Should().BeNull();
        greska.Should().NotBeNull();
    }

    // ── Pretraga ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_BlokiranAutor_Nevidljiv()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        (await PretragaAsync(majstorId)).Items.Should().ContainSingle(j => j.Id == posaoId);

        await WithService<BlockService, (bool, string?)>(svc => svc.BlokirajAsync(autorId, majstorId));

        (await PretragaAsync(majstorId)).Items.Should().BeEmpty();
        (await JaviSeAsync(posaoId, majstorId)).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Pretraga_UklonjenPosao_Nevidljiv()
    {
        var (_, majstorId, posaoId) = await PosaoSaMajstoromAsync();

        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ModerationState, ModerationState.Removed)));

        (await PretragaAsync(majstorId)).Items.Should().BeEmpty();
    }

    // ── Javljanje ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Javljanje_BezNalogaUslugodavca_Odbijeno()
    {
        var (_, _, posaoId) = await PosaoSaMajstoromAsync();
        var obican = await Data.CreateConfirmedUserAsync("obican@test.rs");

        var (prijava, _, greska) = await JaviSeAsync(posaoId, obican.Id);

        prijava.Should().BeNull();
        greska.Should().Contain("uslugodav");
    }

    [Fact]
    public async Task Javljanje_NaSvojPosao_Odbijeno()
    {
        var (autor, _) = await Data.CreateProviderAsync("autor-majstor@test.rs");
        var posaoId    = await ObjaviAsync(autor.Id);

        (await JaviSeAsync(posaoId, autor.Id)).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Javljanje_Dvaput_JedanRed_IKarticaUCetu()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();

        var (prijava, razgovorId, greska) = await JaviSeAsync(posaoId, majstorId, ponuda: 2500m);
        prijava.Should().NotBeNull(greska);
        razgovorId.Should().NotBeNull();

        (await JaviSeAsync(posaoId, majstorId)).Error.Should().NotBeNull();
        (await Query(db => db.JobApplications.CountAsync())).Should().Be(1);

        var kartica = await Query(db => db.Messages.SingleAsync(m => m.ConversationId == razgovorId));
        kartica.Kind.Should().Be(MessageKind.JobApplication);
        kartica.JobApplicationId.Should().Be(prijava!.Id);

        (await ObavestenjaAsync(autorId, NotificationKind.JobApplicationReceived)).Should().Be(1);
        (await ObavestenjaAsync(autorId, NotificationKind.NewMessage))
            .Should().Be(0, "kartica ide bez opšteg „Nova poruka\" — autor već dobija preciznije");
    }

    // ── Izbor ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Izbor_OstaliObavesteni_JavljanjaOstajuPoslata()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (drugi, _) = await Data.CreateProviderAsync("drugi@test.rs");
        var (prva, _, _)  = await JaviSeAsync(posaoId, majstorId);
        var (druga, _, _) = await JaviSeAsync(posaoId, drugi.Id);

        await IzaberiAsync(posaoId, autorId, prva!.Id);

        var posao = await PosaoAsync(posaoId);
        posao.Status.Should().Be(JobStatus.Assigned);
        posao.Applications.Single(a => a.Id == druga!.Id).Status.Should().Be(JobApplicationStatus.Sent);
        (await ObavestenjaAsync(drugi.Id, NotificationKind.JobNotChosen)).Should().Be(1);
        (await ObavestenjaAsync(majstorId, NotificationKind.JobChosen)).Should().Be(1);

        // Dogovoren posao nestaje iz pretrage i ne prima javljanja.
        (await PretragaAsync(drugi.Id)).Items.Should().BeEmpty();
        var (treci, _) = await Data.CreateProviderAsync("treci@test.rs");
        (await JaviSeAsync(posaoId, treci.Id)).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Izbor_SamoAutor()
    {
        var (_, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (prijava, _, _) = await JaviSeAsync(posaoId, majstorId);

        var (_, greska) = await WithService<JobApplicationService, (int?, string?)>(svc =>
            svc.AssignAsync(posaoId, majstorId, prijava!.Id));

        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task DvaIstovremenaIzbora_JedanPrelaz()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (drugi, _) = await Data.CreateProviderAsync("drugi@test.rs");
        var (prva, _, _)  = await JaviSeAsync(posaoId, majstorId);
        var (druga, _, _) = await JaviSeAsync(posaoId, drugi.Id);

        var rezultati = await Task.WhenAll(
            WithService<JobApplicationService, (int?, string?)>(svc => svc.AssignAsync(posaoId, autorId, prva!.Id)),
            WithService<JobApplicationService, (int?, string?)>(svc => svc.AssignAsync(posaoId, autorId, druga!.Id)));

        rezultati.Count(r => r.Item2 is null).Should().Be(1);
        (await Query(db => db.JobApplications.CountAsync(a => a.Status == JobApplicationStatus.Chosen)))
            .Should().Be(1);
    }

    // ── Otkazivanje i odustajanje ──────────────────────────────────────────

    [Fact]
    public async Task Otkazivanje_PosaoOpetAktivan_OtkazaniSePonovoJavlja()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (drugi, _) = await Data.CreateProviderAsync("drugi@test.rs");
        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await JaviSeAsync(posaoId, drugi.Id);
        await IzaberiAsync(posaoId, autorId, prva!.Id);

        var (ok, greska) = await WithService<JobApplicationService, (bool, string?)>(svc =>
            svc.UnassignAsync(posaoId, autorId, "Nije došao"));
        ok.Should().BeTrue(greska);

        var posao = await PosaoAsync(posaoId);
        posao.Status.Should().Be(JobStatus.Open);
        posao.CancelCount.Should().Be(1);
        posao.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(29), "vreme roka je vraćeno kakvo je bilo pri izboru");
        posao.Applications.Single(a => a.Id == prva.Id).Status.Should().Be(JobApplicationStatus.Cancelled);
        (await ObavestenjaAsync(drugi.Id, NotificationKind.JobReopened)).Should().Be(1);
        (await PretragaAsync(drugi.Id)).Items.Should().ContainSingle();

        var (ponovo, _, g2) = await JaviSeAsync(posaoId, majstorId);
        ponovo.Should().NotBeNull(g2);
        ponovo!.Id.Should().Be(prva.Id, "ponovno javljanje menja isti red");
        (await Query(db => db.JobApplications.SingleAsync(a => a.Id == prva.Id))).IsReapplied.Should().BeTrue();
    }

    [Fact]
    public async Task Otkazivanje_RokNajmanjeTriDana()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAt, DateTime.UtcNow.AddHours(5))));
        await IzaberiAsync(posaoId, autorId, prva!.Id);

        await WithService<JobApplicationService, (bool, string?)>(svc => svc.UnassignAsync(posaoId, autorId, null));

        (await PosaoAsync(posaoId)).ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddDays(3).AddMinutes(-1));
    }

    [Fact]
    public async Task CetvrtoOtkazivanje_Odbijeno()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();

        for (var i = 0; i < 3; i++)
        {
            var (prijava, _, g) = await JaviSeAsync(posaoId, majstorId);
            prijava.Should().NotBeNull(g);
            await IzaberiAsync(posaoId, autorId, prijava!.Id);
            (await WithService<JobApplicationService, (bool, string?)>(svc => svc.UnassignAsync(posaoId, autorId, null)))
                .Item1.Should().BeTrue();
        }

        var (cetvrta, _, _) = await JaviSeAsync(posaoId, majstorId);
        await IzaberiAsync(posaoId, autorId, cetvrta!.Id);

        var (ok, greska) = await WithService<JobApplicationService, (bool, string?)>(svc =>
            svc.UnassignAsync(posaoId, autorId, null));

        ok.Should().BeFalse();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task Odustajanje_VracaPosao_NeBrojiSeUOtkazivanja()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await IzaberiAsync(posaoId, autorId, prva!.Id);

        var (ok, greska) = await WithService<JobApplicationService, (bool, string?)>(svc => svc.QuitAsync(posaoId, majstorId));

        ok.Should().BeTrue(greska);
        var posao = await PosaoAsync(posaoId);
        posao.Status.Should().Be(JobStatus.Open);
        posao.CancelCount.Should().Be(0);
        (await ObavestenjaAsync(autorId, NotificationKind.JobWorkerQuit)).Should().Be(1);
    }

    // ── Istek ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Istek_ObavestenjeJednom_PaIstekao_PaTrajnoObrisan()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAt, DateTime.UtcNow.AddHours(20))));

        await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync());
        await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync());
        (await ObavestenjaAsync(autorId, NotificationKind.JobExpiringSoon)).Should().Be(1);

        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        var ishod = await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync());

        ishod.Isteklo.Should().Be(1);
        (await PosaoAsync(posaoId)).Status.Should().Be(JobStatus.Expired);
        (await PretragaAsync(majstorId)).Items.Should().BeEmpty();
        (await WithService<JobService, JobDto?>(svc => svc.GetAsync(posaoId, majstorId))).Should().BeNull();
        (await WithService<JobService, JobDto?>(svc => svc.GetAsync(posaoId, autorId))).Should().NotBeNull();

        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PurgeAt, DateTime.UtcNow.AddMinutes(-1))));
        (await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync())).Obrisano.Should().Be(1);
        (await Query(db => db.Jobs.AnyAsync(j => j.Id == posaoId))).Should().BeFalse();
    }

    [Fact]
    public async Task Produzenje_VanProzora_Odbijeno_UProzoru_Prolazi()
    {
        var (autorId, _, posaoId) = await PosaoSaMajstoromAsync();

        var (_, greska) = await WithService<JobService, (DateTime?, string?)>(svc => svc.ExtendAsync(posaoId, autorId));
        greska.Should().NotBeNull("do isteka ima 30 dana");

        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAt, DateTime.UtcNow.AddDays(2))));
        var (istice, g2) = await WithService<JobService, (DateTime?, string?)>(svc => svc.ExtendAsync(posaoId, autorId));

        istice.Should().NotBeNull(g2);
        istice!.Value.Should().BeAfter(DateTime.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task PrijavljenPosao_SeNeBriseTrajno()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (ok, greska) = await WithService<ReportService, (bool, string?)>(svc => svc.PrijaviAsync(majstorId,
            new DTOs.Moderation.CreateReportDto { TargetType = ReportTargetType.Job, JobId = posaoId, Reason = ReportReason.Neprikladno }));
        ok.Should().BeTrue(greska);

        await WithService<JobService, (bool, string?)>(svc => svc.DeleteAsync(posaoId, autorId));
        await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync());

        var posao = await Query(db => db.Jobs.SingleAsync(j => j.Id == posaoId));
        posao.Status.Should().Be(JobStatus.Deleted);
        posao.Description.Should().BeEmpty("lični podaci se ne drže ni za prijavljen posao");
    }

    // ── Izvršen ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Izvrsen_PreRoka_Odbijen()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await IzaberiAsync(posaoId, autorId, prva!.Id);

        var (nagrada, greska) = await WithService<JobApplicationService, (decimal?, string?)>(svc =>
            svc.CompleteAsync(posaoId, autorId));

        nagrada.Should().BeNull();
        greska.Should().NotBeNull();
    }

    [Fact]
    public async Task Izvrsen_TokeniTacnoJednom_PosaoNestaje()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await IzaberiAsync(posaoId, autorId, prva!.Id);
        await PomeriIzborAsync(posaoId, dana: 8);

        var pre = (await Data.GetTokenBalanceAsync(autorId), await Data.GetTokenBalanceAsync(majstorId));

        var rezultati = await Task.WhenAll(
            WithService<JobApplicationService, (decimal?, string?)>(svc => svc.CompleteAsync(posaoId, autorId)),
            WithService<JobApplicationService, (decimal?, string?)>(svc => svc.CompleteAsync(posaoId, autorId)));

        rezultati.Count(r => r.Item2 is null).Should().Be(1);
        (await Data.GetTokenBalanceAsync(autorId)).Should().Be(pre.Item1 + 3m);
        (await Data.GetTokenBalanceAsync(majstorId)).Should().Be(pre.Item2 + 5m);
        (await Query(db => db.TokenTransactions.CountAsync(t => t.Kind == TokenKind.JobReward))).Should().Be(2);

        var posao = await PosaoAsync(posaoId);
        posao.Status.Should().Be(JobStatus.Completed);
        posao.Description.Should().BeEmpty();
        (await WithService<JobService, JobDto?>(svc => svc.GetAsync(posaoId, autorId))).Should().BeNull();
        var moji = await WithService<JobService, MyJobsDto>(svc => svc.MineAsync(autorId));
        (moji.Active.Count + moji.Assigned.Count + moji.Expired.Count).Should().Be(0);
    }

    // ── Ocene posla ────────────────────────────────────────────────────────

    [Fact]
    public async Task OceneposlaObeStrane_UlazeUProsekeUlogu()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await IzaberiAsync(posaoId, autorId, prva!.Id);
        await PomeriIzborAsync(posaoId, dana: 8);
        await WithService<JobApplicationService, (decimal?, string?)>(svc => svc.CompleteAsync(posaoId, autorId));

        Task<(ReviewDto?, string?)> Oceni(string ko, int zvezdice) =>
            WithService<ReviewService, (ReviewDto?, string?)>(svc => svc.CreateAsync(ko,
                new CreateReviewDto { JobId = posaoId, Stars = zvezdice }));

        var (odAutora, g1) = await Oceni(autorId, 5);
        odAutora.Should().NotBeNull(g1);
        odAutora!.IsPublished.Should().BeFalse();

        var treci = await Data.CreateConfirmedUserAsync("treci@test.rs");
        (await Oceni(treci.Id, 1)).Item2.Should().NotBeNull("samo strane u poslu ocenjuju");

        var (odMajstora, g2) = await Oceni(majstorId, 4);
        odMajstora.Should().NotBeNull(g2);
        odMajstora!.IsPublished.Should().BeTrue();

        var profil = await Query(db => db.ProviderProfiles.SingleAsync(p => p.UserId == majstorId));
        profil.AverageRating.Should().Be(5m, "ocena izvršioca ide u isti prosek kao ocene usluga");
        var autor = await Query(db => db.Users.SingleAsync(u => u.Id == autorId));
        autor.ClientRating.Should().Be(4m);
        autor.ClientReviewCount.Should().Be(1);

        // Posao se kasnije trajno briše — ocena ostaje, sa naslovom.
        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PurgeAt, DateTime.UtcNow.AddMinutes(-1))));
        await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync());

        var klijentske = await WithService<ReviewService, ClientReviewsDto?>(svc =>
            svc.GetClientReviewsAsync(autorId, 1, 20, null));
        klijentske!.Items.Should().ContainSingle(r => r.ListingTitle == "Treba okačiti police");
    }

    // ── Isticanje ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Isticanje_BezTokena_Odbijeno()
    {
        var (autorId, _, posaoId) = await PosaoSaMajstoromAsync();

        var (ok, _) = await WithService<BoostService, (bool, string?)>(svc =>
            svc.BoostJobAsync(posaoId, autorId, new BoostJobDto { TokensToSpend = 3, DurationDays = 3 }));

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Isticanje_PosebnaSekcija_PauzaDokJeDogovoren()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        await Data.SetTokenBalanceAsync(autorId, 10m);

        var (ok, greska) = await WithService<BoostService, (bool, string?)>(svc =>
            svc.BoostJobAsync(posaoId, autorId, new BoostJobDto { TokensToSpend = 3, DurationDays = 3 }));
        ok.Should().BeTrue(greska);
        (await Data.GetTokenBalanceAsync(autorId)).Should().Be(7m);

        (await PretragaAsync(majstorId, istaknuti: true)).Items.Should().ContainSingle();
        (await PretragaAsync(majstorId, istaknuti: false)).Items.Should().BeEmpty("istaknut se ne ponavlja u običnoj listi");

        var (prva, _, _) = await JaviSeAsync(posaoId, majstorId);
        await IzaberiAsync(posaoId, autorId, prva!.Id);
        var dogovoren = await PosaoAsync(posaoId);
        dogovoren.IsBoosted.Should().BeFalse();
        dogovoren.BoostRemainingOnAssign.Should().NotBeNull();

        await WithService<JobApplicationService, (bool, string?)>(svc => svc.UnassignAsync(posaoId, autorId, null));
        (await PretragaAsync(majstorId, istaknuti: true)).Items.Should().ContainSingle("isticanje se nastavlja");

        // Kad isticanje prođe, održavanje gasi zastavicu.
        await Query(db => db.Jobs.Where(j => j.Id == posaoId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.BoostExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        await WithService<JobOdrzavanje, JobOdrzavanje.Ishod>(svc => svc.ProdjiAsync());
        (await PosaoAsync(posaoId)).IsBoosted.Should().BeFalse();
        (await PretragaAsync(majstorId, istaknuti: false)).Items.Should().ContainSingle();
    }

    // ── Brisanje naloga ────────────────────────────────────────────────────

    [Fact]
    public async Task BrisanjeNaloga_PosloviObrisani_JavljanjaPovucena()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        await JaviSeAsync(posaoId, majstorId);

        var drugiAutor = await Data.CreateConfirmedUserAsync("drugi-autor@test.rs");
        var drugiPosao = await ObjaviAsync(drugiAutor.Id, "Košenje trave u dvorištu");
        await JaviSeAsync(drugiPosao, majstorId);

        // Autor briše nalog → posao obrisan, javljeni obavešteni.
        (await WithService<UserService, (bool, string?)>(svc =>
            svc.DeleteAccountAsync(autorId, TestData.DefaultPassword))).Item1.Should().BeTrue();
        (await Query(db => db.Jobs.AnyAsync(j => j.Id == posaoId))).Should().BeFalse("neprijavljen posao se briše odmah");
        (await ObavestenjaAsync(majstorId, NotificationKind.JobRemoved)).Should().Be(1);

        // Uslugodavac briše nalog → njegovo javljanje povučeno.
        (await WithService<UserService, (bool, string?)>(svc =>
            svc.DeleteAccountAsync(majstorId, TestData.DefaultPassword))).Item1.Should().BeTrue();
        (await Query(db => db.JobApplications.SingleAsync(a => a.JobId == drugiPosao)))
            .Status.Should().Be(JobApplicationStatus.Withdrawn);
    }

    // ── Kartica javljanja u četu ───────────────────────────────────────────

    [Fact]
    public async Task KarticaJavljanja_PratiIzbor_IzabranIDrugi()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var (drugi, _) = await Data.CreateProviderAsync("drugi@test.rs", fullName: "Drugi Majstor");
        var (prijava, razgovorId, _)  = await JaviSeAsync(posaoId, majstorId);
        var (_, drugiRazgovorId, _)   = await JaviSeAsync(posaoId, drugi.Id);

        async Task<DTOs.Conversations.JobCardDto> KarticaAsync(int razgovor, string gledalac)
        {
            var (poruke, greska) = await WithService<ConversationService, (List<DTOs.Conversations.MessageDto>?, string?)>(
                svc => svc.GetMessagesAsync(razgovor, gledalac));
            poruke.Should().NotBeNull(greska);
            return poruke!.Single(m => m.Job is not null).Job!;
        }

        (await KarticaAsync(razgovorId!.Value, autorId)).ApplicationStatus.Should().Be("Sent");

        await IzaberiAsync(posaoId, autorId, prijava!.Id);

        var izabran = await KarticaAsync(razgovorId.Value, majstorId);
        izabran.ApplicationStatus.Should().Be("Chosen");
        izabran.JobStatus.Should().Be("Assigned");
        izabran.ChosenOther.Should().BeFalse();

        var ostali = await KarticaAsync(drugiRazgovorId!.Value, drugi.Id);
        ostali.ApplicationStatus.Should().Be("Sent");
        ostali.ChosenOther.Should().BeTrue();
    }

    // ── Demo seed ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DemoSeedPoslovi_SvaStanja_Idempotentno()
    {
        // Ide kroz prave servise — pukne li neko pravilo, pukne i demo seed.
        var konfiguracija = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoSeed:Enabled"]  = "true",
            ["DemoSeed:Password"] = TestData.DefaultPassword
        }).Build();
        await WithService<IServiceProvider>(sp => DemoSeed.RunAsync(sp, konfiguracija, NullLogger.Instance));

        await WithService<IServiceProvider>(sp => DemoSeedPoslovi.RunAsync(sp, NullLogger.Instance));
        await WithService<IServiceProvider>(sp => DemoSeedPoslovi.RunAsync(sp, NullLogger.Instance));

        var stanja = await Query(db => db.Jobs.GroupBy(j => j.Status)
            .Select(g => new { g.Key, Broj = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Broj));
        stanja[JobStatus.Open].Should().Be(4);
        stanja[JobStatus.Assigned].Should().Be(2);
        stanja[JobStatus.Expired].Should().Be(1);
        stanja[JobStatus.Completed].Should().Be(2);

        var jovana = await Query(db => db.Users.SingleAsync(u => u.Email == DemoSeed.KlijentEmail));
        var cekaOcenu = await WithService<ReviewService, List<DTOs.Bookings.PendingReviewDto>>(svc => svc.PendingAsync(jovana.Id));
        cekaOcenu.Should().ContainSingle(p => p.Kind == "Job" && p.OtherSideReviewed);
    }

    [Fact]
    public async Task DemoSeedSlikeUCetu_DveSlikeJovanaSanji_Idempotentno()
    {
        // Najmanji JPEG sa SOF0 zaglavljem (640 × 480), pod imenima iz demo foldera.
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xD9
        ];
        var folder = Directory.CreateTempSubdirectory("demo-slike-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "torte-1.jpg"), jpeg);
            await File.WriteAllBytesAsync(Path.Combine(folder, "torte-2.jpg"), jpeg);

            var konfiguracija = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DemoSeed:Enabled"]    = "true",
                ["DemoSeed:Password"]   = TestData.DefaultPassword,
                ["DemoSeed:ImagesPath"] = folder
            }).Build();
            await WithService<IServiceProvider>(sp => DemoSeed.RunAsync(sp, konfiguracija, NullLogger.Instance));
            await WithService<IServiceProvider>(sp => DemoSeedPoslovi.RunAsync(sp, NullLogger.Instance));

            await WithService<IServiceProvider>(sp => DemoSeedSlikeUCetu.RunAsync(sp, konfiguracija, NullLogger.Instance));
            await WithService<IServiceProvider>(sp => DemoSeedSlikeUCetu.RunAsync(sp, konfiguracija, NullLogger.Instance));

            var jovana = await Query(db => db.Users.SingleAsync(u => u.Email == DemoSeed.KlijentEmail));
            var slike = await Query(db => db.Messages
                .Where(m => m.Kind == MessageKind.Image && m.SenderId == jovana.Id)
                .Select(m => m.Attachment!.StoragePath).ToListAsync());
            slike.Should().HaveCount(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ── Prijava i moderacija ───────────────────────────────────────────────

    [Fact]
    public async Task AdminUklanjaPosao_AutorObavesten_PosaoNestaje()
    {
        var (autorId, majstorId, posaoId) = await PosaoSaMajstoromAsync();
        var admin = await Data.CreateAdminAsync();
        await WithService<ReportService, (bool, string?)>(svc => svc.PrijaviAsync(majstorId,
            new DTOs.Moderation.CreateReportDto { TargetType = ReportTargetType.Job, JobId = posaoId, Reason = ReportReason.Neprikladno }));

        var red = await WithService<ReportService, List<DTOs.Moderation.ReportQueueItemDto>>(svc => svc.RedAsync());
        red.Should().ContainSingle(r => r.JobId == posaoId && r.VlasnikId == autorId);

        var (ok, greska) = await WithService<ReportService, (bool, string?)>(svc => svc.ResiAsync(admin.Id,
            new DTOs.Moderation.ResolveReportDto
            {
                TargetType = ReportTargetType.Job,
                JobId      = posaoId,
                Action     = DTOs.Moderation.ReportAction.UkloniPosao
            }));

        ok.Should().BeTrue(greska);
        (await ObavestenjaAsync(autorId, NotificationKind.JobRemoved)).Should().Be(1);
        (await PretragaAsync(majstorId)).Items.Should().BeEmpty();
        (await Query(db => db.Reports.CountAsync(r => r.Status == ReportStatus.Pending))).Should().Be(0);
    }
}
