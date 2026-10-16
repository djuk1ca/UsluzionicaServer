using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Infrastructure.Demo;

/// <summary>
/// Demo slike u četu — dve fotografije torti koje Jovana šalje Sanji uz
/// javljanje na „Torta za rođendan" (plan „Slike u četu" §10, za snimke ekrana).
///
/// Ide kroz PRAVI <see cref="ChatImageService"/> (provera formata, moderacija,
/// šifrovanje, poruka preko huba). Slike su iz <c>DemoSeed:ImagesPath</c>; bez
/// tog foldera korak se preskače. Zaseban od <see cref="DemoSeedPoslovi"/> i sa
/// sopstvenom proverom, pa radi i nad demo bazom u kojoj poslovi već postoje.
/// Kao i prave, brišu se posle 14 dana — sledeći start ih tada ponovo pošalje.
/// </summary>
public static class DemoSeedSlikeUCetu
{
    private static readonly (string Fajl, string? Opis)[] Slike =
    [
        ("torte-1.jpg", "Čokoladna sa malinama, prošle subote"),
        ("torte-2.jpg", null)
    ];

    public static async Task RunAsync(IServiceProvider sp, IConfiguration config, ILogger logger)
    {
        var folder = config["DemoSeed:ImagesPath"];
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            logger.LogInformation("DemoSeedSlikeUCetu: bez slika (DemoSeed:ImagesPath nije zadat ili ne postoji).");
            return;
        }

        var db = sp.GetRequiredService<AppDbContext>();
        var domen = "@" + DemoSeed.Domen;

        var jovana = await db.Users.Where(u => u.Email == "demo" + domen).Select(u => u.Id).SingleOrDefaultAsync();
        var sanja  = await db.Users.Where(u => u.Email == "sanja.m" + domen).Select(u => u.Id).SingleOrDefaultAsync();
        if (jovana is null || sanja is null) return;

        var razgovorId = await db.Conversations
            .Where(c => (c.User1Id == jovana && c.User2Id == sanja) || (c.User1Id == sanja && c.User2Id == jovana))
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();
        if (razgovorId is not int id) return;

        if (await db.MessageAttachments.AnyAsync(a => a.Message.ConversationId == id))
        {
            logger.LogInformation("DemoSeedSlikeUCetu: slike već postoje, preskačem.");
            return;
        }

        foreach (var (ime, opis) in Slike)
        {
            var putanja = Path.Combine(folder, ime);
            if (!File.Exists(putanja)) continue;

            // Svaka slika u svom scope-u, kao jedan HTTP zahtev.
            await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            await using var tok = File.OpenRead(putanja);
            var fajl = new FormFile(tok, 0, tok.Length, "file", ime)
            {
                Headers     = new HeaderDictionary(),
                ContentType = "image/jpeg"
            };

            var (_, greska, _) = await scope.ServiceProvider.GetRequiredService<ChatImageService>()
                .PosaljiAsync(id, jovana, fajl, opis);
            if (greska is not null)
                logger.LogWarning("DemoSeedSlikeUCetu: slika {Fajl} nije poslata: {Greska}", ime, greska);
        }
    }
}
