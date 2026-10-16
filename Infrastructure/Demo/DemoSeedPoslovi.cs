using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Jobs;
using UsluzionicaServer.DTOs.Reviews;
using UsluzionicaServer.Persistence;
using UsluzionicaServer.Services;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.Infrastructure.Demo;

/// <summary>
/// Demo poslovi — svako stanje iz životnog ciklusa bar jednom, da se ekrani
/// mogu proći i snimiti sa demo naloga (<see cref="DemoSeed.KlijentEmail"/>,
/// Jovana: i autor i uslugodavac).
///
/// Ide kroz PRAVE servise (objava, javljanje, izbor, izvršen, ocena), pa
/// kartice javljanja u četu, obaveštenja, tokeni i ocene nastaju istim putem
/// kao u aplikaciji; posle se samo vremena pomeraju unazad.
///
/// Zaseban od <see cref="DemoSeed"/> i sa sopstvenom proverom: radi i nad demo
/// bazom napravljenom pre poslova, bez brisanja postojećih podataka.
///
/// Svaki korak u SVOM scope-u, kao jedan HTTP zahtev: servisi menjaju stanje
/// uslovnim ExecuteUpdate-om, a deljen DbContext bi sledećem koraku vratio
/// zastareo praćen entitet.
/// </summary>
public static class DemoSeedPoslovi
{
    private const int Vodoinstalater = 71, Elektricar = 72, Moler = 73, Ciscenje = 134, Selidbe = 137, Torte = 187;

    public static async Task RunAsync(IServiceProvider sp, ILogger logger)
    {
        var db = sp.GetRequiredService<AppDbContext>();

        if (await db.Jobs.AnyAsync(j => j.Author.Email!.EndsWith("@" + DemoSeed.Domen)))
        {
            logger.LogInformation("DemoSeedPoslovi: poslovi već postoje, preskačem.");
            return;
        }

        var scopes = sp.GetRequiredService<IServiceScopeFactory>();
        async Task<T> U<TServis, T>(Func<TServis, Task<T>> korak) where TServis : notnull
        {
            await using var scope = scopes.CreateAsyncScope();
            return await korak(scope.ServiceProvider.GetRequiredService<TServis>());
        }

        async Task<string> Id(string email) =>
            await db.Users.Where(u => u.Email == email + "@" + DemoSeed.Domen).Select(u => u.Id).SingleAsync();

        var jovana = await Id("demo");
        var sanja  = await Id("sanja.m");
        var luka   = await Id("luka.b");
        var tamara = await Id("tamara.n");
        var marko  = await Id("marko.p");
        var nikola = await Id("nikola.v");
        var stefan = await Id("stefan.t");
        var milica = await Id("milica.r");

        async Task<int> Objavi(string autor, int kategorija, string naslov, string opis, decimal? plata)
        {
            var (job, greska) = await U<JobService, (JobDto?, string?)>(s => s.CreateAsync(autor, new CreateJobDto
            {
                Title = naslov, CategoryId = kategorija, Location = "Novi Sad", Description = opis, PayAmount = plata
            }));
            return job?.Id ?? throw new InvalidOperationException($"DemoSeedPoslovi: „{naslov}\": {greska}");
        }

        async Task<int> JaviSe(int posao, string uslugodavac, string poruka, decimal? ponuda)
        {
            var (prijava, _, greska) = await U<JobApplicationService, (MyApplicationDto?, int?, string?)>(s =>
                s.ApplyAsync(posao, uslugodavac, new ApplyToJobDto { Message = poruka, OfferAmount = ponuda }));
            return prijava?.Id ?? throw new InvalidOperationException($"DemoSeedPoslovi: javljanje na {posao}: {greska}");
        }

        async Task Izaberi(int posao, string autor, int prijava, int danaUnazad)
        {
            var (_, greska) = await U<JobApplicationService, (int?, string?)>(s => s.AssignAsync(posao, autor, prijava));
            if (greska is not null) throw new InvalidOperationException($"DemoSeedPoslovi: izbor na {posao}: {greska}");
            await db.Jobs.Where(j => j.Id == posao)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.AssignedAt, DateTime.UtcNow.AddDays(-danaUnazad)));
        }

        async Task Izvrsi(int posao, string autor, int danaUnazad)
        {
            var (_, greska) = await U<JobApplicationService, (decimal?, string?)>(s => s.CompleteAsync(posao, autor));
            if (greska is not null) throw new InvalidOperationException($"DemoSeedPoslovi: izvršen {posao}: {greska}");
            var kada = DateTime.UtcNow.AddDays(-danaUnazad);
            await db.Jobs.Where(j => j.Id == posao).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.CompletedAt, kada)
                .SetProperty(j => j.PurgeAt, kada.AddDays(14)));
        }

        async Task Oceni(int posao, string autor, int zvezdice, string komentar)
        {
            var (_, greska) = await U<ReviewService, (ReviewDto?, string?)>(s =>
                s.CreateAsync(autor, new CreateReviewDto { JobId = posao, Stars = zvezdice, Comment = komentar }));
            if (greska is not null) throw new InvalidOperationException($"DemoSeedPoslovi: ocena {posao}: {greska}");
        }

        // ── Aktivni, tuđi — Jovana ih vidi kao uslugodavac ─────────────────
        var slavina = await Objavi(luka, Vodoinstalater, "Zamena slavine u kupatilu",
            "Slavina na lavabou curi i kad je zatvorena. Imam novu slavinu, treba samo zameniti. Liman 3, treći sprat sa liftom.", 2500m);
        await JaviSe(slavina, marko, "Mogu sutra između 10 i 12h, ponesite i ključ od šahta.", 2500m);
        // Istaknut — iz Lukinih tokena, kroz pravi BoostService.
        var (istaknut, _) = await U<BoostService, (bool, string?)>(s =>
            s.BoostJobAsync(slavina, luka, new BoostJobDto { TokensToSpend = 3, DurationDays = 3 }));
        if (!istaknut) logger.LogInformation("DemoSeedPoslovi: Luka nema tokena za isticanje, posao ostaje neistaknut.");

        var torta = await Objavi(sanja, Torte, "Torta za rođendan, 30 osoba",
            "Rođendan je u subotu. Volela bih čokoladnu tortu sa malinama, bez fondana. Preuzimam sama na Grbavici.", null);
        await JaviSe(torta, jovana, "Pravim čokoladnu sa malinama, 30 parčadi je 3 kg. Mogu da pošaljem slike prethodnih.", 6500m);

        var krecenje = await Objavi(tamara, Moler, "Krečenje dvosobnog stana",
            "Dvosoban stan od 54 m², zidovi su u dobrom stanju, samo bela boja. Nameštaj ću pomeriti sama.", 25000m);
        await db.Jobs.Where(j => j.Id == krecenje)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAt, DateTime.UtcNow.AddHours(20)));

        // ── Jovanini (autor) — svako stanje ────────────────────────────────
        var frizider = await Objavi(jovana, Selidbe, "Prevoz frižidera do Detelinare",
            "Frižider sa zamrzivačem, visok 180 cm, sa Limana na Detelinaru. Drugi sprat bez lifta na obe adrese.", null);
        await JaviSe(frizider, stefan, "Imam kombi i pomoćnika, može u četvrtak popodne.", 4000m);
        await JaviSe(frizider, nikola, "Mogu u subotu ujutru, kombi je moj.", 3500m);

        var tv = await Objavi(jovana, Elektricar, "Montaža TV-a na zid",
            "TV od 55 inča, nosač imam. Zid je od gipsa, treba proveriti gde idu instalacije.", 3000m);
        var tvNikola = await JaviSe(tv, nikola, "Radim montaže na gips sa odgovarajućim tiplovima, imam detektor kablova.", 3000m);
        await Izaberi(tv, jovana, tvNikola, danaUnazad: 8);   // „Posao je izvršen" je već dostupno

        var ciscenje = await Objavi(jovana, Ciscenje, "Generalno čišćenje stana posle krečenja",
            "Jednosoban stan, treba oprati prozore i podove posle krečenja.", 4500m);
        await JaviSe(ciscenje, milica, "Mogu ceo stan za jedan dan, sredstva nosim ja.", 4500m);
        var istekao = DateTime.UtcNow.AddDays(-2);
        await db.Jobs.Where(j => j.Id == ciscenje).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Expired)
            .SetProperty(j => j.ExpiresAt, istekao)
            .SetProperty(j => j.ExpiredAt, istekao)
            .SetProperty(j => j.PurgeAt, istekao.AddDays(7)));

        // ── Jovana kao izvršilac ───────────────────────────────────────────
        var krstenje = await Objavi(tamara, Torte, "Torta za krštenje",
            "Krštenje je za dve nedelje, oko 20 gostiju. Bela torta sa vanilom, natpis po dogovoru.", 5000m);
        var krstenjeJovana = await JaviSe(krstenje, jovana, "Može bela sa vanilom i šumskim voćem, natpis ručno.", 5000m);
        await Izaberi(krstenje, tamara, krstenjeJovana, danaUnazad: 1);

        // Izvršen, Luka je ocenio Jovanu, Jovana još nije → „Čeka ocenu".
        var kolaci = await Objavi(luka, Torte, "Kolači za slavu, 50 komada",
            "Sitni kolači za slavu, mešano. Preuzimam u petak.", 4000m);
        var kolaciJovana = await JaviSe(kolaci, jovana, "Mogu mešane sitne kolače, 50 komada za petak.", 4000m);
        await Izaberi(kolaci, luka, kolaciJovana, danaUnazad: 10);
        await Izvrsi(kolaci, luka, danaUnazad: 2);
        await Oceni(kolaci, luka, 5, "Kolači su nestali prvi, svi su pitali ko ih je pravio.");

        // Izvršen i ocenjen sa obe strane — ocena naručioca (Sanja) i izvršioca (Stefan).
        var ormar = await Objavi(sanja, Selidbe, "Sklapanje ormara iz IKEA",
            "Ormar PAX, dva krila, delovi su u stanu. Treba sklopiti i pričvrstiti za zid.", 3500m);
        var ormarStefan = await JaviSe(ormar, stefan, "Sklapao sam desetak PAX ormara, treba mi oko dva sata.", 3500m);
        await Izaberi(ormar, sanja, ormarStefan, danaUnazad: 12);
        await Izvrsi(ormar, sanja, danaUnazad: 4);
        await Oceni(ormar, sanja, 5, "Brzo i uredno, ormar je čvrsto pričvršćen.");
        await Oceni(ormar, stefan, 5, "Sve pripremljeno, tačno opisan posao, plaćeno odmah.");

        logger.LogWarning("DemoSeedPoslovi: napravljeno {Broj} demo poslova u svim stanjima.",
            await db.Jobs.CountAsync());
    }
}
