using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Javni poslovi (<c>/api/public/jobs</c>) — sajt ih prikazuje gostu.
///
/// Ide kroz pravi HTTP, bez tokena: bitno je baš to da ruta prolazi anonimno,
/// da oblik odgovora ne nosi ništa privatno i da zatvoren posao izgleda isto
/// kao nepostojeći.
/// </summary>
public class PublicJobsTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Nedostupan = "Ovaj posao više nije dostupan.";

    private async Task<(string AutorId, int PosaoId)> ObjaviAsync(
        string email      = "autor@test.rs",
        string ime        = "Sanja Marković",
        int    kategorija = TestData.SeededCategoryId,
        string grad       = TestData.ValidCity)
    {
        var autor = await Data.CreateConfirmedUserAsync(email, fullName: ime);
        var (job, greska) = await WithService<JobService, (JobDto?, string?)>(svc =>
            svc.CreateAsync(autor.Id, new CreateJobDto
            {
                Title       = "Treba okačiti police",
                CategoryId  = kategorija,
                Location    = grad,
                Description = "Tri police u dnevnoj sobi, zid je od cigle.",
                PayAmount   = 3000m
            }));
        job.Should().NotBeNull(greska);
        return (autor.Id, job!.Id);
    }

    private async Task<JsonElement> DataAsync(HttpResponseMessage odgovor)
    {
        odgovor.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await odgovor.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<List<int>> IdsAsync(string upit = "")
    {
        var data = await DataAsync(await Factory.CreateClient().GetAsync("/api/public/jobs" + upit));
        return data.GetProperty("items").EnumerateArray().Select(j => j.GetProperty("id").GetInt32()).ToList();
    }

    private Task PromeniAsync(int posaoId, JobStatus status, DateTime? istice = null) =>
        Query(db => db.Jobs.Where(j => j.Id == posaoId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.ExpiresAt, j => istice ?? j.ExpiresAt)));

    // ── Pretraga ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pretraga_BezTokena_VracaAktivanPosao_BezPrivatnihPodataka()
    {
        var (autorId, posaoId) = await ObjaviAsync();

        var odgovor = await Factory.CreateClient().GetAsync("/api/public/jobs");
        var sirovo  = await odgovor.Content.ReadAsStringAsync();
        var data    = await DataAsync(odgovor);

        data.GetProperty("total").GetInt32().Should().Be(1);
        data.GetProperty("page").GetInt32().Should().Be(1);
        data.GetProperty("pageSize").GetInt32().Should().Be(20);

        var posao = data.GetProperty("items")[0];
        posao.GetProperty("id").GetInt32().Should().Be(posaoId);
        posao.GetProperty("location").GetString().Should().Be(TestData.ValidCity);
        posao.GetProperty("payAmount").GetDecimal().Should().Be(3000m);

        var autor = posao.GetProperty("author");
        autor.GetProperty("fullName").GetString().Should().Be("Sanja M.");
        autor.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(["fullName", "rating", "ratingCount", "isVerified"]);

        posao.TryGetProperty("myApplication", out _).Should().BeFalse();
        posao.TryGetProperty("isMine", out _).Should().BeFalse();
        sirovo.Should().NotContain(autorId).And.NotContain("autor@test.rs").And.NotContain("Marković");
    }

    [Fact]
    public async Task Pretraga_DogovorenIstekaoUklonjen_Nevidljivi()
    {
        var (_, aktivan)    = await ObjaviAsync("a@test.rs");
        var (_, dogovoren)  = await ObjaviAsync("b@test.rs");
        var (_, istekao)    = await ObjaviAsync("c@test.rs");
        var (_, rokProsao)  = await ObjaviAsync("d@test.rs");
        var (_, uklonjen)   = await ObjaviAsync("e@test.rs");

        await PromeniAsync(dogovoren, JobStatus.Assigned);
        await PromeniAsync(istekao,   JobStatus.Expired);

        // Rok prošao, a održavanje ga još nije prebacilo u Expired.
        await PromeniAsync(rokProsao, JobStatus.Open, DateTime.UtcNow.AddMinutes(-1));
        await Query(db => db.Jobs.Where(j => j.Id == uklonjen)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ModerationState, ModerationState.Removed)));

        (await IdsAsync()).Should().Equal(aktivan);
    }

    [Fact]
    public async Task Pretraga_NadkategorijaObuhvataPodkategorije_GradBezDijakritike()
    {
        // 14 = „Frizerstvo — žene", podkategorija „beauty".
        var (_, posaoId) = await ObjaviAsync(kategorija: 14, grad: "Čačak");

        (await IdsAsync("?categorySlug=beauty")).Should().Equal(posaoId);
        (await IdsAsync("?categorySlug=frizerstvo-zene")).Should().Equal(posaoId);
        (await IdsAsync("?categorySlug=handmade")).Should().BeEmpty();

        (await IdsAsync("?city=cacak")).Should().Equal(posaoId);
        (await IdsAsync("?city=Subotica")).Should().BeEmpty();
    }

    [Fact]
    public async Task Pretraga_Istaknuti_UPosebnojSekciji()
    {
        var (_, obican)    = await ObjaviAsync("a@test.rs");
        var (_, istaknut)  = await ObjaviAsync("b@test.rs");
        await Query(db => db.Jobs.Where(j => j.Id == istaknut).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.IsBoosted, true)
            .SetProperty(j => j.BoostExpiresAt, DateTime.UtcNow.AddDays(3))));

        (await IdsAsync("?boosted=true")).Should().Equal(istaknut);
        (await IdsAsync("?boosted=false")).Should().Equal(obican);
    }

    [Fact]
    public async Task Pretraga_ViewerUserIdIzUpita_Ignorisan()
    {
        // Autor je blokirao gledaoca; da upit može da postavi gledaoca, posao bi nestao.
        var (autorId, posaoId) = await ObjaviAsync();
        var gledalac = await Data.CreateConfirmedUserAsync("gledalac@test.rs");
        await WithService<UsluzionicaServer.Services.BlockService, (bool, string?)>(svc =>
            svc.BlokirajAsync(autorId, gledalac.Id));

        (await IdsAsync($"?viewerUserId={gledalac.Id}")).Should().Equal(posaoId);
    }

    // ── Detalj ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Detalj_BezTokena_VracaPosao()
    {
        var (_, posaoId) = await ObjaviAsync();

        var data = await DataAsync(await Factory.CreateClient().GetAsync($"/api/public/jobs/{posaoId}"));

        data.GetProperty("id").GetInt32().Should().Be(posaoId);
        data.GetProperty("title").GetString().Should().Be("Treba okačiti police");
        data.GetProperty("author").GetProperty("fullName").GetString().Should().Be("Sanja M.");
    }

    [Fact]
    public async Task Detalj_NepostojeciDogovorenIstekao_IstiOdgovor404()
    {
        var (_, dogovoren) = await ObjaviAsync("a@test.rs");
        var (_, istekao)   = await ObjaviAsync("b@test.rs");
        var (_, rokProsao) = await ObjaviAsync("c@test.rs");
        await PromeniAsync(dogovoren, JobStatus.Assigned);
        await PromeniAsync(istekao,   JobStatus.Expired);
        await PromeniAsync(rokProsao, JobStatus.Open, DateTime.UtcNow.AddMinutes(-1));

        var client = Factory.CreateClient();
        var tela = new List<string>();
        foreach (var id in new[] { 999_999, dogovoren, istekao, rokProsao })
        {
            var odgovor = await client.GetAsync($"/api/public/jobs/{id}");
            odgovor.StatusCode.Should().Be(HttpStatusCode.NotFound, $"posao {id}");
            tela.Add(await odgovor.Content.ReadAsStringAsync());
        }

        tela.Distinct().Should().ContainSingle("gost ne sme da razazna koji je slučaj");
        using var doc = JsonDocument.Parse(tela[0]);
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("message").GetString().Should().Be(Nedostupan);
    }

    [Fact]
    public async Task PostojeciApiPoslova_IdaljeTraziPrijavu()
    {
        var odgovor = await Factory.CreateClient().GetAsync("/api/jobs");
        odgovor.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Rate limit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimit_PosleKvote_429()
    {
        // UseSetting, ne ConfigureAppConfiguration: Program.cs čita limit PRE
        // builder.Build(), a ConfigureAppConfiguration iz WithWebHostBuilder
        // stiže tek tada — limiter bi zadržao 100000.
        await using var ograniceno = Factory.WithWebHostBuilder(b =>
            b.UseSetting("RateLimit:PublicPermitLimit", "2"));
        var client = ograniceno.CreateClient();

        (await client.GetAsync("/api/public/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/public/jobs/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/public/jobs")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
