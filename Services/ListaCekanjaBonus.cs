using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Bonus za listu čekanja: 10 tokena svakome ko se upisao na sajtu pre izlaska
/// aplikacije i otvori nalog ISTOM adresom.
///
/// To je obećanje sa sajta (usluzionica.rs — modal, poziv na dnu, „Česta
/// pitanja"), pa pravila ovde moraju da prate tamošnji tekst.
///
/// DVE LISTE, DVA IZLASKA. Aplikacija izlazi posebno za Android i za iPhone.
/// Posle izlaska za Android sajt nudi preuzimanje, a lista čekanja ostaje za
/// iPhone — sa istim bonusom i sopstvenim rokom. Svaka prijava zato pripada
/// jednoj listi (<see cref="ListaZa"/>), a za svaku listu važi:
///
///   • upis PRE izlaska te platforme          → Waitlist:AndroidLaunchDate / IosLaunchDate
///   • nalog otvoren u roku posle izlaska     → Waitlist:ClaimDays (60)
///   • „istom adresom"                        → adresa naloga = adresa sa liste
///
/// Dok datum izlaska nije upisan, ta platforma još nije izašla: svaki upis na
/// njenu listu važi i rok još ne teče.
///
/// Bonus je jedan po nalogu, ne po platformi — isti nalog radi na oba telefona.
///
/// SAMO POTVRĐENA ADRESA. Bez toga bi bilo ko mogao da otvori nalog sa tuđom
/// adresom sa liste i uzme njene tokene. Zato se bonus proverava na istim
/// mestima gde i prva referral rata — kad adresa POSTANE potvrđena (klik na
/// link, reset lozinke, Google prijava) — i u periodičnom prolazu za naloge
/// koji su bili potvrđeni pre nego što se adresa pojavila na listi.
///
/// Kao ReferralService: idempotentno, bezbedno pod istovremenim pozivima, i
/// nikad ne baca — greška u bonusu ne sme da obori potvrdu mejla.
/// </summary>
public sealed class ListaCekanjaBonus(
    AppDbContext               db,
    ListaCekanja               lista,
    NotificationService        notifications,
    IConfiguration             config,
    ILogger<ListaCekanjaBonus> logger)
{
    public const string Android = "android";
    public const string Ios     = "ios";

    public decimal Iznos => config.GetValue("Waitlist:BonusTokens", 10m);

    public int RokDana => config.GetValue("Waitlist:ClaimDays", 60);

    public DateTime? IzlazakAndroid => Datum("Waitlist:AndroidLaunchDate");
    public DateTime? IzlazakIos     => Datum("Waitlist:IosLaunchDate");

    public DateTime? Izlazak(string listaPrijave) => listaPrijave == Ios ? IzlazakIos : IzlazakAndroid;

    /// <summary>Poslednji dan za bonus sa te liste, ili null dok platforma nije izašla.</summary>
    public DateTime? Rok(string listaPrijave) => Izlazak(listaPrijave)?.AddDays(RokDana);

    private bool Ukljucen => lista.Putanja is not null && Iznos > 0;

    private DateTime? Datum(string kljuc) =>
        DateTime.TryParse(config[kljuc], CultureInfo.InvariantCulture,
                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                          out var d) ? d : null;

    /// <summary>
    /// Kojoj listi prijava pripada:
    ///   1. sajt je izričito upisao listu → ta lista;
    ///   2. upis POSLE izlaska za Android → iPhone (sajt tada nudi Android
    ///      preuzimanje, a formu samo za iPhone);
    ///   3. upis sa iPhone-a pre izlaska za Android → iPhone (taj čovek
    ///      Android verziju nije ni mogao da instalira);
    ///   4. sve ostalo → Android.
    /// </summary>
    public string ListaZa(ListaCekanja.Prijava p)
    {
        if (p.Lista is Android or Ios) return p.Lista;
        if (IzlazakAndroid is { } android && p.Kreirano > android) return Ios;
        if (p.Platforma == Ios) return Ios;
        return Android;
    }

    /// <summary>Da li prijava SADA donosi bonus po pravilima svoje liste.</summary>
    private bool Vazi(ListaCekanja.Prijava p, DateTime sada)
    {
        var l = ListaZa(p);
        if (Izlazak(l) is { } izlazak && p.Kreirano > izlazak) return false;   // upis posle izlaska
        if (Rok(l) is { } rok && sada > rok) return false;                       // rok za nalog istekao
        return true;
    }

    /// <summary>
    /// Dodeljuje bonus korisniku ako mu je potvrđena adresa na listi i ako ga
    /// još nije dobio. Vraća true samo kad je bonus upravo isplaćen.
    /// </summary>
    public async Task<bool> PokusajAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            if (!Ukljucen) return false;

            var korisnik = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Id, u.Email, u.EmailConfirmed })
                .FirstOrDefaultAsync(ct);

            if (korisnik is null || !korisnik.EmailConfirmed || korisnik.Email is null) return false;

            var sada = DateTime.UtcNow;
            if (lista.Nadji(korisnik.Email) is not { } prijava) return false;
            if (!Vazi(prijava, sada)) return false;

            var hes = ListaCekanja.Hes(korisnik.Email);
            if (await db.WaitlistBonuses.AnyAsync(w => w.EmailHash == hes || w.UserId == userId, ct))
                return false;

            var iznos = Iznos;

            // ── Rezervacija ────────────────────────────────────────────────
            // Jedinstveni indeksi (EmailHash, UserId) su prava brana: od dva
            // istovremena poziva drugi upis pada na indeksu, i taj poziv ne
            // isplaćuje ništa. Provera iznad je samo da se ne ide u bazu sa
            // upisom koji će sigurno pasti.
            db.WaitlistBonuses.Add(new WaitlistBonus
            {
                EmailHash  = hes,
                UserId     = userId,
                Amount     = iznos,
                SignedUpAt = prijava.Kreirano,
                AwardedAt  = sada
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                logger.LogInformation("Bonus liste čekanja za {UserId} je već isplaćen — preskačem.", userId);
                return false;
            }

            // ── Isplata ────────────────────────────────────────────────────
            // Jednom naredbom (SET balance = balance + @x), kao u ReferralService:
            // `user.TokenBalance += x` bi pregazio isplatu koja istovremeno
            // stiže istom korisniku iz drugog toka.
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenBalance, u => u.TokenBalance + iznos), ct);

            var balansPosle = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.TokenBalance)
                .FirstAsync(ct);

            db.TokenTransactions.Add(new TokenTransaction
            {
                UserId       = userId,
                Amount       = iznos,
                Kind         = TokenKind.WaitlistBonus,
                Description  = "Bonus za prijavu na listu čekanja",
                BalanceAfter = balansPosle,
                CreatedAt    = sada
            });
            await db.SaveChangesAsync(ct);

            await notifications.SendAsync(
                userId, NotificationKind.TokenEarned,
                "Stigao je tvoj bonus",
                $"Hvala što si nas čekao — {iznos:0.##} tokena za prijavu na listu čekanja je na tvom nalogu.");

            // Id, ne mejl — lični podatak ne pripada logu.
            logger.LogInformation("Bonus liste čekanja ({Lista}): {Iznos} tokena korisniku {UserId}.",
                ListaZa(prijava), iznos, userId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bonus liste čekanja nije isplaćen korisniku {UserId}.", userId);
            return false;
        }
    }

    /// <summary>
    /// Prolaz kroz celu listu: isplaćuje bonus svim POTVRĐENIM nalozima čija je
    /// adresa na listi, a bonus još nisu dobili. Hvata naloge koji su bili
    /// potvrđeni pre nego što je adresa stigla na listu (ili pre ovog deploya).
    /// Vraća broj isplaćenih bonusa.
    /// </summary>
    public async Task<int> ProdjiAsync(CancellationToken ct = default)
    {
        if (!Ukljucen) return 0;

        var sada = DateTime.UtcNow;
        var adrese = lista.Prijave()
            .Where(p => Vazi(p.Value, sada))
            .Select(p => p.Key.ToUpperInvariant())   // Identity: NormalizedEmail je velikim slovima
            .ToList();

        if (adrese.Count == 0) return 0;

        var isplaceno = 0;

        // Po 500 — IN lista u SQL-u ima granicu broja parametara.
        foreach (var deo in adrese.Chunk(500))
        {
            var kandidati = await db.Users.AsNoTracking()
                .Where(u => u.EmailConfirmed
                         && u.NormalizedEmail != null
                         && deo.Contains(u.NormalizedEmail)
                         && !db.WaitlistBonuses.Any(w => w.UserId == u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);

            foreach (var id in kandidati)
                if (await PokusajAsync(id, ct)) isplaceno++;
        }

        if (isplaceno > 0)
            logger.LogInformation("Lista čekanja: isplaćeno {Broj} bonusa u prolazu.", isplaceno);

        return isplaceno;
    }

    /// <summary>Stanje jedne liste za admin pregled.</summary>
    public sealed record StanjeListe(int NaListi, DateTime? Izlazak, DateTime? Rok);

    /// <summary>Brojevi za admin pregled — bez ijedne adrese.</summary>
    public async Task<(bool Ukljucen, decimal Iznos, int Isplaceno, StanjeListe Android, StanjeListe Ios)> StanjeAsync(
        CancellationToken ct = default)
    {
        var poListi = lista.Prijave().Values
            .GroupBy(ListaZa)
            .ToDictionary(g => g.Key, g => g.Count());

        var isplaceno = await db.WaitlistBonuses.CountAsync(ct);

        return (Ukljucen, Iznos, isplaceno,
            new StanjeListe(poListi.GetValueOrDefault(Android), IzlazakAndroid, Rok(Android)),
            new StanjeListe(poListi.GetValueOrDefault(Ios),     IzlazakIos,     Rok(Ios)));
    }
}
