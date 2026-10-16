using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.Infrastructure.ChatMedia;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Slike u četu (Obsidian „Plan — Slike u četu" §10): ko sme da šalje i
/// gleda, šifrovanje na disku, potpisan URL, brisanje posle 14 dana sa diska
/// i iz baze, zadržavanje zbog prijave.
/// </summary>
public class ChatImagesTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>Najmanji JPEG sa SOF0 zaglavljem: 640 × 480.</summary>
    static readonly byte[] Jpeg =
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xD9
    ];

    static IFormFile Slika()
    {
        var ms = new MemoryStream(Jpeg);
        return new FormFile(ms, 0, ms.Length, "file", "foto.jpg") { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };
    }

    async Task<(string A, string B, int Razgovor)> RazgovorAsync()
    {
        var a = await Data.CreateConfirmedUserAsync("ana@test.rs", fullName: "Ana A.");
        var b = await Data.CreateConfirmedUserAsync("boban@test.rs", fullName: "Boban B.");
        var razgovor = await WithService<ChatSender, Conversation>(s => s.RazgovorAsync(a.Id, b.Id));
        return (a.Id, b.Id, razgovor.Id);
    }

    Task<(MessageDto? Poruka, string? Greska, bool NijePronadjeno)> PosaljiAsync(int razgovor, string ko, string? opis = null) =>
        WithService<ChatImageService, (MessageDto?, string?, bool)>(s => s.PosaljiAsync(razgovor, ko, Slika(), opis));

    Task<MessageAttachment> PrilogAsync(int porukaId) =>
        Query(db => db.MessageAttachments.SingleAsync(a => a.MessageId == porukaId));

    string Fajl(MessageAttachment a) => Path.Combine(Factory.ChatMediaPath, a.StoragePath);

    static (Guid Id, string U, long Exp, string Sig) Rastavi(string url)
    {
        var uri = new Uri("https://x" + url);
        var q   = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        return (Guid.Parse(uri.Segments[^1]), q["u"]!, long.Parse(q["exp"]!), q["sig"]!);
    }

    Task<byte[]?> ProcitajAsync(string url)
    {
        var (id, u, exp, sig) = Rastavi(url);
        return WithService<ChatImageService, byte[]?>(s => s.ProcitajAsync(id, u, exp, sig));
    }

    /// <summary>Pomera poruku u prošlost — za rok od 14 dana.</summary>
    Task StarostAsync(int porukaId, int dana) =>
        Query(db => db.Messages.Where(m => m.Id == porukaId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SentAt, DateTime.UtcNow.AddDays(-dana))));

    // ── Slanje ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ucesnik_SaljeSliku_FajlJeSifrovan_IDesifrujeSeIsto()
    {
        var (a, b, razgovor) = await RazgovorAsync();

        var (poruka, greska, _) = await PosaljiAsync(razgovor, a, "Evo slavine");

        poruka.Should().NotBeNull(greska);
        poruka!.Kind.Should().Be("Image");
        poruka.Caption.Should().Be("Evo slavine");
        poruka.Image!.Width.Should().Be(640);
        poruka.Image.Height.Should().Be(480);

        var prilog = await PrilogAsync(poruka.Id);
        var naDisku = await File.ReadAllBytesAsync(Fajl(prilog));
        naDisku.Take(2).Should().Equal((byte)'C', (byte)'M');
        naDisku.Should().NotContainInOrder(Jpeg.Skip(20).Take(12), "na disku je šifrat, ne JPEG");

        // Druga strana dobija istu sliku preko svog potpisanog URL-a.
        var (poruke, _) = await WithService<ConversationService, (List<MessageDto>?, string?)>(s => s.GetMessagesAsync(razgovor, b));
        (await ProcitajAsync(poruke!.Single().Image!.ImageUrl)).Should().Equal(Jpeg);

        (await Query(db => db.Notifications.CountAsync(n => n.UserId == b && n.Body == "📷 Evo slavine"))).Should().Be(1);
    }

    [Fact]
    public async Task StariKlijent_DobijaRezervniTekst()
    {
        var (a, _, razgovor) = await RazgovorAsync();

        var (poruka, _, _) = await PosaljiAsync(razgovor, a);

        poruka!.Text.Should().Be(ChatSender.RezervniTekstSlike);
        poruka.Caption.Should().BeEmpty();
    }

    [Fact]
    public async Task TreciNalog_IBlokiran_NeMoguDaSalju()
    {
        var (a, b, razgovor) = await RazgovorAsync();
        var treci = await Data.CreateConfirmedUserAsync("treci@test.rs");

        (await PosaljiAsync(razgovor, treci.Id)).NijePronadjeno.Should().BeTrue();

        await WithService<BlockService, (bool, string?)>(s => s.BlokirajAsync(b, a));
        (await PosaljiAsync(razgovor, a)).NijePronadjeno.Should().BeTrue();
    }

    [Fact]
    public async Task AutomatskaProvera_OdbijaIliZamucuje()
    {
        var (a, _, razgovor) = await RazgovorAsync();

        Factory.ImageModerator.Verdict = ImageVerdict.Reject;
        var (odbijena, greska, _) = await PosaljiAsync(razgovor, a);
        odbijena.Should().BeNull();
        greska.Should().NotBeNull();
        Directory.Exists(Factory.ChatMediaPath).Should().BeFalse("odbijena slika se ni ne upisuje");

        await Data.CreateAdminAsync();   // automatsku prijavu podnosi admin nalog
        Factory.ImageModerator.Verdict = ImageVerdict.Review;
        var (sumnjiva, _, _) = await PosaljiAsync(razgovor, a);
        sumnjiva!.Image!.NeedsReview.Should().BeTrue();
        (await PrilogAsync(sumnjiva.Id)).HeldUntil.Should().NotBeNull();
        (await Query(db => db.Reports.CountAsync(r => r.MessageId == sumnjiva.Id && r.ReportedUserId == a))).Should().Be(1);
    }

    // ── Potpisan URL ───────────────────────────────────────────────────────

    [Fact]
    public async Task PotpisanUrl_LosIliTudjIliIstekao_Odbijen()
    {
        var (a, b, razgovor) = await RazgovorAsync();
        var (poruka, _, _) = await PosaljiAsync(razgovor, a);
        var url = poruka!.Image!.ImageUrl;
        var (id, u, exp, sig) = Rastavi(url);

        (await ProcitajAsync(url)).Should().NotBeNull();

        (await WithService<ChatImageService, byte[]?>(s => s.ProcitajAsync(id, u, exp, sig[..^2] + "AA"))).Should().BeNull("izmenjen potpis");

        var treci = await Data.CreateConfirmedUserAsync("treci@test.rs");
        var tudj  = Factory.Services.GetRequiredService<ChatMediaStore>().PotpisanUrl(id, treci.Id);
        (await ProcitajAsync(tudj)).Should().BeNull("treći nalog nije učesnik ni sa ispravnim potpisom");

        var store = Factory.Services.GetRequiredService<ChatMediaStore>();
        store.PotpisVazi(id, u, exp, sig, DateTime.UtcNow.AddHours(3)).Should().BeFalse("URL je istekao");

        // Isti URL ceo sat — WebView ga kešira.
        store.PotpisanUrl(id, b, new DateTime(2026, 10, 15, 10, 5, 0, DateTimeKind.Utc))
            .Should().Be(store.PotpisanUrl(id, b, new DateTime(2026, 10, 15, 10, 55, 0, DateTimeKind.Utc)));
    }

    // ── Brisanje ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PosleRoka_NemaNiRedaNiFajla_MladjaOstaje()
    {
        var (a, _, razgovor) = await RazgovorAsync();
        var (stara, _, _) = await PosaljiAsync(razgovor, a);
        var (mladja, _, _) = await PosaljiAsync(razgovor, a);
        var stariFajl  = Fajl(await PrilogAsync(stara!.Id));
        var mladjiFajl = Fajl(await PrilogAsync(mladja!.Id));
        await StarostAsync(stara.Id, 15);
        await StarostAsync(mladja.Id, 13);

        var ishod = await WithService<PorukeCiscenje, PorukeCiscenje.Ishod>(s => s.PocistiAsync());

        ishod.Slika.Should().Be(1);
        File.Exists(stariFajl).Should().BeFalse();
        File.Exists(mladjiFajl).Should().BeTrue();
        (await Query(db => db.Messages.AnyAsync(m => m.Id == stara.Id))).Should().BeFalse();
        (await Query(db => db.MessageAttachments.AnyAsync(x => x.MessageId == mladja.Id))).Should().BeTrue();
    }

    [Fact]
    public async Task PrijavljenaSlika_OstajePosleRoka_DokSePrijavaNeResi()
    {
        var (a, b, razgovor) = await RazgovorAsync();
        var (poruka, _, _) = await PosaljiAsync(razgovor, a);
        var fajl = Fajl(await PrilogAsync(poruka!.Id));

        var (ok, greska) = await WithService<ReportService, (bool, string?)>(s => s.PrijaviAsync(b, new CreateReportDto
        {
            TargetType = ReportTargetType.User, ReportedUserId = a, MessageId = poruka.Id, Reason = ReportReason.Neprikladno
        }));
        ok.Should().BeTrue(greska);

        await StarostAsync(poruka.Id, 20);
        await WithService<PorukeCiscenje, PorukeCiscenje.Ishod>(s => s.PocistiAsync());
        File.Exists(fajl).Should().BeTrue("zadržana zbog prijave");
        (await WithService<ChatImageService, byte[]?>(s => s.ProcitajZaAdminaAsync(poruka.Id))).Should().Equal(Jpeg);

        var admin = await Data.CreateAdminAsync();
        await WithService<ReportService, (bool, string?)>(s => s.ResiAsync(admin.Id, new ResolveReportDto
        {
            TargetType = ReportTargetType.User, ReportedUserId = a, Action = ReportAction.Odbij
        }));
        await WithService<PorukeCiscenje, PorukeCiscenje.Ishod>(s => s.PocistiAsync());

        File.Exists(fajl).Should().BeFalse("posle rešene prijave odlazi sa porukom");
        (await Query(db => db.Reports.SingleAsync())).MessageId.Should().BeNull("prijava ostaje, poruka ne");
    }

    [Fact]
    public async Task Prijava_TudjePoruke_Odbijena()
    {
        var (a, b, razgovor) = await RazgovorAsync();
        var (poruka, _, _) = await PosaljiAsync(razgovor, a);
        var treci = await Data.CreateConfirmedUserAsync("treci@test.rs");

        var (ok, _) = await WithService<ReportService, (bool, string?)>(s => s.PrijaviAsync(treci.Id, new CreateReportDto
        {
            TargetType = ReportTargetType.User, ReportedUserId = a, MessageId = poruka!.Id, Reason = ReportReason.Neprikladno
        }));

        ok.Should().BeFalse("ko nije u razgovoru ne može da zadrži tuđu poruku");
    }

    [Fact]
    public async Task Siroce_UStaromDanu_Obrisano_UDanasnjem_Ne()
    {
        var staro    = Path.Combine(Factory.ChatMediaPath, "2026", "01", "02", $"{Guid.NewGuid():N}.bin");
        var danasnje = Path.Combine(Factory.ChatMediaPath, ChatMediaStore.RelativnaPutanja(Guid.NewGuid(), DateTime.UtcNow));
        foreach (var f in new[] { staro, danasnje })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            await File.WriteAllBytesAsync(f, [1, 2, 3]);
        }

        var ishod = await WithService<PorukeCiscenje, PorukeCiscenje.Ishod>(s => s.PocistiAsync());

        ishod.Sirocica.Should().Be(1);
        File.Exists(staro).Should().BeFalse();
        Directory.Exists(Path.Combine(Factory.ChatMediaPath, "2026", "01")).Should().BeFalse("prazne fascikle se brišu");
        File.Exists(danasnje).Should().BeTrue("današnji fajl je možda usred slanja");
        File.Delete(danasnje);
    }

    [Fact]
    public async Task BrisanjeNaloga_NjegoveSlikeNestajuOdmah()
    {
        var (a, b, razgovor) = await RazgovorAsync();
        var (njegova, _, _) = await PosaljiAsync(razgovor, a);
        var (tudja, _, _) = await PosaljiAsync(razgovor, b);
        var njegovFajl = Fajl(await PrilogAsync(njegova!.Id));

        (await WithService<UserService, (bool, string?)>(s => s.DeleteAccountAsync(a, TestData.DefaultPassword))).Item1.Should().BeTrue();

        File.Exists(njegovFajl).Should().BeFalse();
        (await Query(db => db.MessageAttachments.AnyAsync(x => x.MessageId == njegova.Id))).Should().BeFalse();
        (await Query(db => db.MessageAttachments.AnyAsync(x => x.MessageId == tudja!.Id))).Should().BeTrue();

        // Poruka ostaje do isteka — bez slike („Slika više nije dostupna").
        var (poruke, _) = await WithService<ConversationService, (List<MessageDto>?, string?)>(s => s.GetMessagesAsync(razgovor, b));
        poruke!.Single(p => p.Id == njegova.Id).Image.Should().BeNull();
    }
}
